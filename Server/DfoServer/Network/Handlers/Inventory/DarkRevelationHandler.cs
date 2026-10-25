using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class DarkRevelationHandler(InventoryDarkRevelationService service, DarkRevelationCatalog revelations,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.MULTIPLE_ACTION_EQUIP_OPTION, Handle, nameof(DarkRevelationHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        DarkRevelationRequest request;
        try { request = EquipmentUpgradeRequests.ReadDarkRevelation(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.MULTIPLE_ACTION_EQUIP_OPTION,
                CargoPackets.Ack(InventoryResult.Reject(1)), "equipment_dark_revelation_malformed");
            return CommandResult.Handled;
        }
        // 装备脚本在提交锁外解析；事务内只读取已缓存的不可变资格与费用。
        _ = revelations.Weapon(request.ItemId);
        var result = context.BlocksTownActions ? InventoryResult.Reject(0x16) :
            context.Session.PlanAndCommitInventory(character => service.Apply(character, request), 1);
        var receipt = result.Receipt;
        log.Write("equipment_dark_revelation", new
        {
            Connection = context.Id, request.Slot, request.ItemId, request.PowerSlot, request.Ability,
            result.Accepted, result.Error, Revision = receipt?.Character.Revision
        });
        await projection.RefreshAsync(context, result);
        // 00651A80 的成功分支只消费公共成功头，使用客户端待提交选择刷新窗口。
        await context.Reply(CmdPacketType.MULTIPLE_ACTION_EQUIP_OPTION, CargoPackets.Ack(result),
            result.Accepted ? "equipment_dark_revelation_committed" : "equipment_dark_revelation_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
