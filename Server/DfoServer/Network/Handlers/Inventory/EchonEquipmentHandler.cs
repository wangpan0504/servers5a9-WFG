using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EchonEquipmentHandler(InventoryEchonEquipmentService service, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CUSTOM_ABILITY_EQUIP_OPTION, Handle, nameof(EchonEquipmentHandler));
        commands.Register(CmdPacketType.CUSTOM_ABILITY_SET_EQUIP_OPTION, Handle, nameof(EchonEquipmentHandler));
        commands.Register(CmdPacketType.CUSTOM_ABILITY_UPGRADE, Handle, nameof(EchonEquipmentHandler));
    }
    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var opcode = (CmdPacketType)packet.Opcode;
        InventoryResult result;
        Func<DfoServer.Game.Characters.Character, InventoryResult>? plan = null;
        try
        {
            switch (opcode)
            {
                case CmdPacketType.CUSTOM_ABILITY_EQUIP_OPTION:
                    var option = EchonEquipmentRequests.Option(packet.Body);
                    plan = character => service.ChangeOption(character, option);
                    break;
                case CmdPacketType.CUSTOM_ABILITY_SET_EQUIP_OPTION:
                    var set = EchonEquipmentRequests.Set(packet.Body);
                    plan = character => service.AssignSet(character, set);
                    break;
                default:
                    short slot = EchonEquipmentRequests.Upgrade(packet.Body);
                    plan = character => service.Upgrade(character, slot);
                    break;
            }
        }
        catch (InvalidDataException)
        {
            plan = null;
        }
        result = plan is null || context.BlocksTownActions ? InventoryResult.Reject(11) :
            context.Session.PlanAndCommitInventory(plan, 11);
        var receipt = result.Receipt;
        log.Write("echon_equipment", new { Connection = context.Id, Opcode = packet.Opcode,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision });
        await projection.RefreshAsync(context, result);
        await context.Reply(opcode, CargoPackets.Ack(result), result.Accepted ? "echon_equipment_committed" : "echon_equipment_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
