using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> DecreaseDurabilityAsync(GameConnectionContext context, Packet packet)
    {
        byte slot = InventoryRequests.DurabilityLoss(packet.Body);
        var result = context.Session.PlanAndCommitInventory(character => inventory.DecreaseDurability(character, slot),
            requireTown: false, requiredPhase: SessionPhase.Dungeon);
        if (result.Receipt is { } receipt && result.Plan is { Changed: true })
        {
            // 本版48没有成功ACK；只更新已提交的当前装备，不重建城镇Actor或整张装备表。
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Equipment, [new(InventoryListType.Equipment, slot)], index: receipt.Index),
                "equipment_durability_decreased", () => context.Session.CanPublishInventory(receipt));
        }
        return CommandResult.Handled;
    }
}
