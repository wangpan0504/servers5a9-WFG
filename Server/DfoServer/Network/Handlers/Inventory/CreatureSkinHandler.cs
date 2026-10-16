using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CreatureSkinHandler(InventoryCreatureSkinService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.OPEN_AURA_SKIN_SLOT, Handle, nameof(CreatureSkinHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(captured.Selected, context.Session.Selected) &&
            captured.SelectionEpoch == context.Session.SelectionEpoch && captured.Phase == context.Session.Phase);
        (short SourceSlot, ushort Target) request;
        try { request = CreatureSkinRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("creature_skin_unlock", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.OPEN_AURA_SKIN_SLOT,
                CreatureSkinPackets.Open(InventoryCreatureSkinService.TargetSlot, InventoryResult.Reject(InventoryCreatureSkinService.Rejection)),
                "creature_skin_unlock_malformed", SameSelection);
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryCreatureSkinService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.PlanUnlock(character, request.SourceSlot, request.Target,
                service.Clock.GetUtcNow().ToUnixTimeSeconds()), InventoryCreatureSkinService.Rejection);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        log.Write("creature_skin_unlock", new { Connection = context.Id, Slot = request.SourceSlot, request.Target,
            result.Accepted, result.Error, Revision = result.Receipt?.Character.Revision });
        // 成功回调只驱动界面动画；先安装权威库存和开通状态，再允许该回调读取它们。
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.OPEN_AURA_SKIN_SLOT, CreatureSkinPackets.Open(request.Target, result),
            result.Accepted ? "creature_skin_unlock_committed" : "creature_skin_unlock_rejected", Guard);
        return CommandResult.Handled;
    }
}
