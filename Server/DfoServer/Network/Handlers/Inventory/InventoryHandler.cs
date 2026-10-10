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
        var result = context.Session.MutateInventory(character => inventory.Move(character, request),
            requireTown: IsCargo(request.Source.List) || IsCargo(request.Destination.List));
        await context.Reply(CmdPacketType.MOVE_ITEMSPACE, InventoryPackets.Move(request, result), result.Accepted ? "move_item_committed" : "move_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> SortAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Sort(packet.Body);
        var result = context.Session.MutateInventory(character => inventory.Sort(character, request), requireTown: IsCargo(request.List));
        await context.Reply(CmdPacketType.SORT_ITEM, InventoryPackets.Sort(request, result), result.Accepted ? "sort_item_committed" : "sort_item_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeleteAsync(GameConnectionContext context, Packet packet)
    {
        InventoryDeleteRequest request;
        try
        {
            request = InventoryRequests.Delete(packet.Body);
        }
        catch (InvalidDataException)
        {
            request = new(InventoryListType.Main, [], 0);
            await context.Reply(CmdPacketType.DELETE_ITEM,
                InventoryPackets.Delete(request, InventoryResult.Reject(), InventorySnapshot.Empty),
                "delete_item_malformed");
            return CommandResult.Handled;
        }
        var result = context.Session.MutateInventory(character => inventory.Delete(character, request), requireTown: IsCargo(request.List));
        var current = result.Receipt?.Character.Inventory ?? InventorySnapshot.Empty;
        await context.Reply(CmdPacketType.DELETE_ITEM, InventoryPackets.Delete(request, result, current),
            result.Accepted ? "delete_item_committed" : "delete_item_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        // 材料余额已由 ACK18 更新；重复发送 NOTI14 会把剩余数量显示成获得物品。
        var acknowledgedSlots = request.Items.Where(item => item.Type == 2)
            .Select(item => new InventorySlot(request.List, item.Slot)).ToHashSet();
        await projection.RefreshAsync(context, result, acknowledgedSlots);
        return CommandResult.Handled;
    }

    private static bool IsCargo(InventoryListType list) => list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;
}
