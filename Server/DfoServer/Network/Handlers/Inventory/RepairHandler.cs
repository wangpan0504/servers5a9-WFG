using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class RepairHandler(InventoryRepairService repair, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REPAIR_EQUIPMENT, Handle, nameof(RepairHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = RepairRequests.Read(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
            context.Session.RepairEquipment(request, repair);
        bool CanSend() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(CmdPacketType.REPAIR_EQUIPMENT, RepairPackets.Ack(request, result),
            result.Accepted ? "equipment_repaired" : "equipment_repair_rejected", CanSend);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
