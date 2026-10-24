using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class InventoryExpansionHandler(InventoryExpansionService expansion, InventoryProjection projection, ServerLog log)
    : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.UPGRADE_INVENTORY, Upgrade, nameof(InventoryExpansionHandler));

    async Task<CommandResult> Upgrade(GameConnectionContext context, Packet packet)
    {
        ushort slot = InventoryExpansionRequests.Read(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        InventoryResult result;
        try
        {
            result = context.BlocksTownActions ? InventoryResult.Reject(InventoryExpansionService.Rejection) :
                context.Session.PlanAndCommitInventory(character => expansion.Plan(character, slot), InventoryExpansionService.Rejection);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            log.Write("inventory_expansion_resource_rejected", new { Connection = context.Id, Slot = slot, Error = error.Message });
            result = InventoryResult.Reject(InventoryExpansionService.Rejection);
        }
        bool CanSend() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.UPGRADE_INVENTORY, InventoryExpansionPackets.Ack(result),
            result.Accepted ? "inventory_expansion_committed" : "inventory_expansion_rejected", CanSend);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
