using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarCompoundHandler(InventoryAvatarCompoundService compounds, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.COMPOUND_AVATAR, Handle, nameof(AvatarCompoundHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        AvatarCompoundRequest request;
        try { request = AvatarCompoundRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("avatar_compound", new
            {
                Connection = context.Id,
                Length = packet.Body.Length,
                Accepted = false,
                Reason = "malformed"
            });
            await context.Reply(CmdPacketType.COMPOUND_AVATAR, AvatarCompoundPackets.Failure(),
                "avatar_compound_malformed");
            return CommandResult.Handled;
        }

        var original = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        AvatarCompoundReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions
            ? InventoryResult.Reject(22)
            : context.Session.CompoundAvatar(request, compounds, out receipt, out rejection);
        rejection ??= result.Accepted ? null : "blocked-state";
        bool CanSend() => result.Receipt is { } committed
            ? context.Session.CanPublishInventory(committed)
            : context.Owner.Execute(() => context.Session.SelectionEpoch == original.SelectionEpoch &&
                context.Session.Phase == original.Phase && ReferenceEquals(context.Session.Selected, original.Selected));

        InventoryItem[] outputs = [];
        if (result.Accepted)
        {
            var committed = result.Receipt ?? throw new InvalidOperationException("装扮合成成功缺少提交回执");
            if (receipt is null)
                throw new InvalidOperationException("装扮合成成功缺少业务回执");
            outputs = receipt.Outputs.Select(output =>
            {
                var item = committed.Index.Find(output.Position);
                return item is { Avatar: not null } && item.Core.ItemId == output.ItemId
                    ? item : throw new InvalidOperationException("装扮合成提交产出与规划不一致");
            }).ToArray();
        }
        log.Write("avatar_compound", new
        {
            Connection = context.Id,
            result.Accepted,
            result.Error,
            Reason = result.Accepted ? "committed" : rejection,
            Part = receipt?.Part,
            Material = receipt?.MaterialItemId,
            MaterialType = receipt?.MaterialType,
            RareRate = receipt?.RareRate,
            RareRoll = receipt?.RareRoll,
            Rare = receipt?.HitRare,
            Outputs = receipt?.Outputs.Length,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.COMPOUND_AVATAR, AvatarCompoundPackets.Failure(),
                "avatar_compound_rejected", CanSend);
            return CommandResult.Handled;
        }

        await context.Reply(CmdPacketType.COMPOUND_AVATAR,
            AvatarCompoundPackets.Success(request, outputs), "avatar_compound_committed", CanSend);
        await projection.RefreshAsync(context, result, result.Plan!.ChangedSlots.ToHashSet());
        return CommandResult.Handled;
    }
}
