using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CreatureEggHandler(InventoryCreatureEggService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.HATCH_CREATURE, Handle, nameof(CreatureEggHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool SameSelection() => context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
            capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        InventorySlot position;
        try { position = CreatureEggRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("creature_hatch", new { Connection = context.Id, Accepted = false, Reason = "malformed", Length = packet.Body.Length });
            await context.Reply(CmdPacketType.HATCH_CREATURE, CreatureEggPackets.Ack(InventoryResult.Reject(InventoryCreatureEggService.Rejection)),
                "creature_hatch_malformed", SameSelection);
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(InventoryCreatureEggService.Rejection) :
            context.Session.PlanAndCommitInventory(character => service.Plan(character, position, service.Clock.GetUtcNow().ToUnixTimeSeconds()),
                InventoryCreatureEggService.Rejection);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : SameSelection();
        log.Write("creature_hatch", new { Connection = context.Id, List = position.List, Slot = position.Index,
            result.Accepted, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.HATCH_CREATURE, CreatureEggPackets.Ack(result),
            result.Accepted ? "creature_hatch_committed" : "creature_hatch_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
