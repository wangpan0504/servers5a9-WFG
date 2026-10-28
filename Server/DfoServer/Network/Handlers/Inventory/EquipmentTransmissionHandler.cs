using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EquipmentTransmissionHandler(InventoryEquipmentTransmissionService service,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REQUEST_EQUIPMENT_TRANSMISSION,
        Transmit, nameof(EquipmentTransmissionHandler));

    async Task<CommandResult> Transmit(GameConnectionContext context, Packet packet)
    {
        const CmdPacketType opcode = CmdPacketType.REQUEST_EQUIPMENT_TRANSMISSION;
        EquipmentTransmissionRequest request;
        try
        {
            request = EquipmentTransmissionRequests.Read(packet.Body);
            service.Prepare(request);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            log.Write("equipment_transmission_rejected", new { Connection = context.Id, Reason = "malformed_or_resource" });
            await context.Reply(opcode, CargoPackets.Ack(InventoryResult.Reject(1)), "equipment_transmission_malformed");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(1) :
            context.Session.PlanAndCommitInventory(character => service.Transmit(character, request), 1);
        var receipt = result.Receipt;
        log.Write("equipment_transmission", new
        {
            Connection = context.Id, request.TargetSlot, request.TargetId, request.DonorSlot, request.DonorId,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision
        });
        await projection.RefreshAsync(context, result);
        await context.Reply(opcode, EquipmentTransmissionPackets.Ack(result, request.TargetSlot, request.TargetId),
            result.Accepted ? "equipment_transmission_committed" : "equipment_transmission_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
