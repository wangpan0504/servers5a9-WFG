using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> LockAsync(GameConnectionContext context, Packet packet, InventoryLockOperation operation)
    {
        var slot = InventoryRequests.Lock(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.MutateInventory(character => inventory.Lock(character, slot, operation), invalidStateError: 19);
        await context.Reply((CmdPacketType)packet.Opcode, InventoryLockPackets.Ack(slot, operation, result),
            result.Accepted ? "item_lock_committed" : "item_lock_rejected");
        if (result.Accepted)
        {
            if (operation == InventoryLockOperation.EquipmentUnlock)
                await context.Notify(NotiPacketType.ITEM_UNLOCK, InventoryLockPackets.Unlocked(slot), "item_unlock_immediate");
            else if (operation is InventoryLockOperation.EquipmentLock or InventoryLockOperation.CancelUnlock)
                await context.Notify(NotiPacketType.ITEM_LOCK_LIST, InventoryLockPackets.Locked([slot]), "item_lock_state");
        }
        return CommandResult.Handled;
    }
}
