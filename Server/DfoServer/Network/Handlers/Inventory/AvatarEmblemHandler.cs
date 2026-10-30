using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class AvatarEmblemHandler(InventoryAvatarEmblemService service,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.USE_EMBLEM, Use, nameof(AvatarEmblemHandler));

    async Task<CommandResult> Use(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool CapturedCurrent() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        AvatarEmblemRequest request;
        try { request = AvatarEmblemRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("avatar_emblem", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Reason = "malformed" });
            await context.Reply(CmdPacketType.USE_EMBLEM, AvatarEmblemPackets.Failure(1), "avatar_emblem_malformed",
                () => capture.Selected is not null && CapturedCurrent());
            return CommandResult.Handled;
        }
        AvatarEmblemReceipt? receipt = null;
        string? rejection = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(InventoryAvatarEmblemService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.Use(character, request, out receipt, out rejection),
                InventoryAvatarEmblemService.Rejection);
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            CapturedCurrent();
        log.Write("avatar_emblem", new
        {
            Connection = context.Id, List = (byte)request.Target.List, Slot = request.Target.Index,
            request.TargetItemId, Count = request.Emblems.Length, result.Accepted, result.Error,
            Reason = result.Accepted ? "committed" : rejection ?? "state-or-snapshot", Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.USE_EMBLEM, AvatarEmblemPackets.Failure(result.Error), "avatar_emblem_rejected", Guard);
            return CommandResult.Handled;
        }
        var committed = result.Receipt ?? throw new InvalidOperationException("装扮镶嵌缺少提交回执");
        var applied = receipt ?? throw new InvalidOperationException("装扮镶嵌缺少业务回执");
        var materials = applied.Materials.Select(material => new AvatarEmblemRemaining(material.Slot,
            committed.Index.Find(new(InventoryListType.Main, material.Slot))?.Core.Count ?? 0)).ToArray();
        var acknowledged = applied.Materials.Select(material => new InventorySlot(InventoryListType.Main, material.Slot)).ToHashSet();
        // ACK201仅更新材料绝对数量；先投影已提交目标的完整五孔详情。
        await projection.RefreshAsync(context, result, acknowledgedSlots: acknowledged);
        await context.Reply(CmdPacketType.USE_EMBLEM, AvatarEmblemPackets.Used(materials),
            "avatar_emblem_committed", () => context.Session.CanPublishInventory(committed));
        return CommandResult.Handled;
    }
}
