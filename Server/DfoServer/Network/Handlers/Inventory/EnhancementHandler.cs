using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EnhancementHandler(InventoryEnhancementService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.UPGRADE_ITEM, Handle, nameof(EnhancementHandler));
    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        EnhancementRequest request;
        try { request = EnhancementRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("enhancement", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.UPGRADE_ITEM, CargoPackets.Ack(InventoryResult.Reject(InventoryEnhancementService.Rejection)), "enhancement_malformed", SameSelection);
            return CommandResult.Handled;
        }
        EnhancementReceipt? receipt = null;
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryEnhancementService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.Plan(character, request, out receipt), InventoryEnhancementService.Rejection);
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) : SameSelection();
        log.Write("enhancement", new { Connection = context.Id, request.Mode, TargetList = request.Target.List,
            TargetSlot = request.Target.Index, request.MaterialSlot, result.Accepted, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.UPGRADE_ITEM, EnhancementPackets.Ack(result, result.Accepted ? receipt : null),
            result.Accepted ? "enhancement_committed" : "enhancement_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
