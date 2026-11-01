using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler(InventoryService inventory, InventoryProjection projection,
    ServerLog? log = null, TradeHandler? trade = null, InventoryKnightShieldService? knightShields = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SYNC_ITEM_SPACE, SynchronizeItemSpaceAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.DECREASE_DURABILITY, DecreaseDurabilityAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.MOVE_ITEMSPACE, MoveAsync, nameof(InventoryHandler));
        if (knightShields is not null)
            commands.Register(CmdPacketType.CHANGE_DECK_INFO, ChangeShieldDeckAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.SORT_ITEM, SortAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.DELETE_ITEM, DeleteAsync, nameof(InventoryHandler));
        commands.Register(CmdPacketType.DROP_ITEM, DiscardAsync, nameof(InventoryHandler));
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
        if (knightShields is not null && InventoryKnightShieldService.Recognizes(request))
            return await MoveShieldAsync(context, request);
        if (trade is not null && await trade.TryMoveAsync(context, packet)) return CommandResult.Handled;
        var result = context.Session.MoveInventory(request, inventory);
        var outcome = !result.Accepted ? "move_item_rejected"
            : result.Plan?.Reason == InventoryMoveService.LoginWearRestatementReason ? "move_item_restatement_absorbed"
            : "move_item_committed";
        await context.Reply(CmdPacketType.MOVE_ITEMSPACE, InventoryPackets.Move(request, result), outcome,
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        // ACK19已转移普通穿戴槽；宠物26–29仍保留专属槽位同步。
        var acknowledgedSlots = result.Plan?.ChangedSlots
            .Where(slot => slot.List == InventoryListType.Equipment && slot.Index is not (>= 26 and <= 29)).ToHashSet();
        InventoryListType? silentMoveList = request.Source.List == request.Destination.List &&
            request.Source.List is InventoryListType.Main or InventoryListType.Pet ? request.Source.List : null;
        bool suppressMainNotice = request.Source.List == InventoryListType.Main &&
            request.Destination is { List: InventoryListType.Equipment, Index: >= 12 and <= 25 } ||
            request.Destination.List == InventoryListType.Main &&
            request.Source is { List: InventoryListType.Equipment, Index: >= 12 and <= 25 };
        bool suppressAvatarNotice = request.Source.List == InventoryListType.Avatar &&
            request.Destination is { List: InventoryListType.Equipment, Index: >= 0 and <= 11 } ||
            request.Destination.List == InventoryListType.Avatar &&
            request.Source is { List: InventoryListType.Equipment, Index: >= 0 and <= 11 };
        bool suppressPetNotice = request.Source.List == InventoryListType.Pet && request.Destination.List == InventoryListType.Equipment ||
            request.Destination.List == InventoryListType.Pet && request.Source.List == InventoryListType.Equipment;
        InventoryListType? suppressMoveNoticeList = suppressMainNotice ? InventoryListType.Main :
            suppressAvatarNotice ? InventoryListType.Avatar : suppressPetNotice ? InventoryListType.Pet : null;
        await projection.RefreshAsync(context, result, acknowledgedSlots, silentMoveList: silentMoveList,
            suppressMoveNoticeList: suppressMoveNoticeList);
        return CommandResult.Handled;
    }

    async Task<CommandResult> SortAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Sort(packet.Body);
        var result = IsCargo(request.List)
            ? context.Session.PlanAndCommitInventory(character => inventory.Sort(character, request), requireTown: true)
            : context.Session.MutateInventory(character => inventory.Sort(character, request), requireTown: false);
        // 页面/条件取自客户端菜单，记录原值用于核对护石等特殊容器的分类语义。
        log?.Write("sort_item", new
        {
            Connection = context.Id,
            List = (byte)request.List,
            Category = (byte)request.Kind,
            request.Condition,
            result.Accepted,
            result.Error,
            Slots = result.Plan?.ChangedSlots.Length ?? 0
        });
        await context.Reply(CmdPacketType.SORT_ITEM, InventoryPackets.Sort(request, result), result.Accepted ? "sort_item_committed" : "sort_item_rejected");
        await projection.RefreshAsync(context, result);
        // 护石窗口与符文页只在容器46全量到达时重新装载；只发1681差量时页面维持旧顺序。
        // 零变化整理按协议只保留ACK，不额外补发全量。
        if (result.Accepted && result.Plan is { Changed: true } && request.List == InventoryListType.Talisman)
            await projection.RefreshListAsync(context, InventoryListType.Talisman, "talisman_list_sync");
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
                InventoryPackets.Delete(request, InventoryResult.Reject()),
                "delete_item_malformed");
            return CommandResult.Handled;
        }
        var result = context.Session.DeleteInventory(request, inventory);
        await context.Reply(CmdPacketType.DELETE_ITEM, InventoryPackets.Delete(request, result),
            result.Accepted ? "delete_item_committed" : "delete_item_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        // ACK18按本次消耗量扣减材料；重复发送NOTI14会触发获得物品提示。
        var acknowledgedSlots = request.Items.Where(item => item.Type is 2 or 3)
            .Select(item => new InventorySlot(request.List, item.Slot)).ToHashSet();
        await projection.RefreshAsync(context, result, acknowledgedSlots);
        return CommandResult.Handled;
    }

    private static bool IsCargo(InventoryListType list) => list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;
}
