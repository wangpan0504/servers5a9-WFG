using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EquipmentUpgradeHandler(InventoryEquipmentUpgradeService service,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REQUEST_EQUIPMENT_UPGRADE, Upgrade, nameof(EquipmentUpgradeHandler));
        commands.Register(CmdPacketType.REQUEST_EQUIPMENT_FUSION, Imprint, nameof(EquipmentUpgradeHandler));
        commands.Register(CmdPacketType.REQUEST_EQUIPMENT_IMPRINT, Imprint, nameof(EquipmentUpgradeHandler));
    }

    async Task<CommandResult> Upgrade(GameConnectionContext context, Packet packet)
    {
        EquipmentUpgradeRequest request;
        try
        {
            request = EquipmentUpgradeRequests.ReadUpgrade(packet.Body);
            var source = context.Owner.Execute(() => context.Session.Selected?.Inventory
                .Find(new(InventoryListType.Main, request.Slot)));
            service.PrepareImprint(source, new(request.Slot, request.ItemId, request.Slot, request.TargetId));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            await context.Reply(CmdPacketType.REQUEST_EQUIPMENT_UPGRADE,
                CargoPackets.Ack(InventoryResult.Reject(1)), "equipment_upgrade_malformed");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(0x16) :
            context.Session.PlanAndCommitInventory(character => service.Upgrade(character, request), 1);
        var receipt = result.Receipt;
        log.Write("equipment_upgrade", new
        {
            Connection = context.Id, request.Slot, request.ItemId, request.TargetId, request.Upgrade,
            request.GenuineUpgrade, request.InheritEnchant, request.InheritEmblems,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision
        });
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.REQUEST_EQUIPMENT_UPGRADE,
            EquipmentUpgradePackets.Upgrade(result, request.Slot, request.ItemId, 0),
            result.Accepted ? "equipment_upgrade_committed" : "equipment_upgrade_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Imprint(GameConnectionContext context, Packet packet)
    {
        var opcode = (CmdPacketType)packet.Opcode;
        EquipmentFusionRequest request;
        try
        {
            request = EquipmentUpgradeRequests.ReadFusion(packet.Body);
            var source = context.Owner.Execute(() => context.Session.Selected?.Inventory
                .Find(new(InventoryListType.Main, request.SourceSlot)));
            service.PrepareImprint(source, request);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            await context.Reply(opcode,
                CargoPackets.Ack(InventoryResult.Reject(1)), "equipment_imprint_malformed");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(0x16) :
            context.Session.PlanAndCommitInventory(character => service.Imprint(character, request,
                weaponFusion: opcode == CmdPacketType.REQUEST_EQUIPMENT_IMPRINT), 1);
        var receipt = result.Receipt;
        log.Write("equipment_imprint", new
        {
            Connection = context.Id, Opcode = packet.Opcode, request.SourceSlot, request.SourceId, request.TargetSlot, request.TargetId,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision
        });
        await projection.RefreshAsync(context, result);
        await context.Reply(opcode,
            EquipmentUpgradePackets.Imprint(result, request.TargetSlot, request.TargetId),
            result.Accepted ? "equipment_imprint_committed" : "equipment_imprint_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
