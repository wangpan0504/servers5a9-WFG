using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler(InventoryService inventory, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MOVE_ITEMSPACE, MoveAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.SORT_ITEM, SortAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.DELETE_ITEM, DeleteAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.USE_GEM, UseGuardianGemAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_LOCK, (context, packet) => LockAsync(context, packet, InventoryLockOperation.EquipmentLock), nameof(InventoryHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_UNLOCK, (context, packet) => LockAsync(context, packet, InventoryLockOperation.EquipmentUnlock), nameof(InventoryHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_UNLOCK_CANCEL, (context, packet) => LockAsync(context, packet, InventoryLockOperation.CancelUnlock), nameof(InventoryHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_SORT_LOCK, (context, packet) => LockAsync(context, packet, InventoryLockOperation.SortLock), nameof(InventoryHandler));
        commands.Register(CmdPacketType.REQUEST_ITEM_SORT_UNLOCK, (context, packet) => LockAsync(context, packet, InventoryLockOperation.SortUnlock), nameof(InventoryHandler));
    }

    async Task<CommandResult> MoveAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Move(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject() : context.Session.MutateInventory(character => inventory.Move(character, request));
        await context.Reply(CmdPacketType.MOVE_ITEMSPACE, InventoryPackets.Move(request, result), result.Accepted ? "move_item_committed" : "move_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> SortAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Sort(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject() :
            context.Session.MutateInventory(character => inventory.Sort(character, request));
        await context.Reply(CmdPacketType.SORT_ITEM, InventoryPackets.Sort(request, result), result.Accepted ? "sort_item_committed" : "sort_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeleteAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Delete(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject() : context.Session.MutateInventory(character => inventory.Delete(character, request));
        var current = context.Session.Selected?.Inventory ?? InventorySnapshot.Empty;
        await context.Reply(CmdPacketType.DELETE_ITEM, InventoryPackets.Delete(request, result, current), result.Accepted ? "delete_item_committed" : "delete_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
