using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class LimitCubeHandler(InventoryLimitCubeService cubes, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.USE_LIMIT_CUBE, Handle, nameof(LimitCubeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        LimitCubeRequest request;
        try { request = LimitCubeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("limit_cube_use", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(CmdPacketType.USE_LIMIT_CUBE, LimitCubePackets.Failure(),
                "limit_cube_malformed");
            return CommandResult.Handled;
        }

        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        int? requestedTicket = original.Selected?.Inventory
            .Find(new(InventoryListType.Main, request.TicketSlot))?.Core.ItemId;
        LimitCubeReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(1)
            : context.Session.UseLimitCube(request, cubes, out receipt, out rejection);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase &&
                ReferenceEquals(context.Session.Selected, original.Selected));

        if (result.Accepted)
        {
            var committed = result.Receipt ??
                throw new InvalidOperationException("变更券成功缺少提交回执");
            if (receipt is null)
                throw new InvalidOperationException("变更券成功缺少业务回执");
            var output = committed.Index.Find(receipt.ResultSlot);
            if (output?.Core.ItemId != receipt.ResultItemId || output.Core.Count < receipt.ResultCount)
                throw new InvalidOperationException("变更券提交产出与规划不一致");
        }
        log.Write("limit_cube_use", new
        {
            Connection = context.Id,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection,
            Target = request.TargetItemId,
            TargetSlot = request.TargetSlot,
            Ticket = receipt?.TicketItemId ?? requestedTicket,
            TicketSlot = request.TicketSlot,
            Result = receipt?.ResultItemId,
            ResultSlot = receipt?.ResultSlot.Index,
            Count = receipt?.ResultCount,
            receipt?.Roll,
            receipt?.TotalWeight,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.USE_LIMIT_CUBE,
                LimitCubePackets.Failure(result.Error), "limit_cube_rejected", CanSend);
            return CommandResult.Handled;
        }

        // ACK消费者会立即按结果槽查背包，必须先发布已提交的NOTI14差量。
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.USE_LIMIT_CUBE,
            LimitCubePackets.Success(receipt!), "limit_cube_committed", CanSend);
        return CommandResult.Handled;
    }
}
