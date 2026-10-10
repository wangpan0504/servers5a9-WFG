using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed class InventoryProjection(CharacterStatComputer stats, TownProjection town, EpicPieceCatalog pieces)
{
    public async Task RefreshAsync(GameConnectionContext context, InventoryResult result, IReadOnlySet<InventorySlot>? acknowledgedSlots = null)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan)
            return;
        var receipt = result.Receipt ?? throw new InvalidOperationException("背包刷新缺少提交回执");
        bool CanSend() => context.Session.CanPublishInventory(receipt);
        if (!CanSend())
            return;
        var current = receipt.Character;
        var changedSlots = plan.ChangedSlots.Concat(plan.ChangedCurrencies.Select(ItemSlotBoundService.CurrencySlot).Where(slot => slot.HasValue)
            .Select(slot => new InventorySlot(InventoryListType.Main, slot!.Value))).Distinct().ToArray();
        var slots = changedSlots.Where(slot => acknowledgedSlots?.Contains(slot) != true).ToArray();
        if (changedSlots.Any(slot => receipt.Index.Find(slot)?.Creature is not null))
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(current.Inventory), "creature_details_committed", CanSend);
        foreach (var list in slots.Select(slot => slot.List).Distinct().Order())
        {
            var replaced = slots.Where(slot => slot.List == list && plan.BeforeIndex.Find(slot) is { } old &&
                receipt.Index.Find(slot) is { } now && old.Core.ItemId != now.Core.ItemId).ToArray();
            if (replaced.Length != 0)
                await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(InventorySnapshot.Empty, list, replaced), "inventory_clear_replaced", CanSend);
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(current.Inventory, list, slots, index: receipt.Index), "inventory_committed_delta", CanSend);
        }
        if (changedSlots.Any(slot => slot.List == InventoryListType.Equipment))
        {
            // 副本及结算沿用当前玩家对象；完整Actor刷新只用于城镇/选图。
            if (receipt.Phase is SessionPhase.Town or SessionPhase.MapSelect)
                await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(current), "equipment_actor", CanSend);
            await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(current, stats.Compute(current)), "equipment_player", CanSend);
            context.Owner.Execute(() =>
            {
                if (CanSend() && context.Session.Phase == SessionPhase.Town)
                    town.Refresh(context);
                return true;
            });
        }
        await RestoreLocksAsync(context, current.Inventory, changedSlots, plan.Before, CanSend, receipt.Index, plan.BeforeIndex);
        if (plan.ChangedCurrencies.Any(kind => kind is >= DfoServer.Game.Currency.CurrencyKind.Cera and <= DfoServer.Game.Currency.CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(current.Inventory.AccountWallet), "currency_committed_delta", CanSend);
        if (plan.ChangedEpicPieces.Length != 0)
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(current.Inventory.EpicPieces, pieces, plan.ChangedEpicPieces), "epic_book_committed_delta", CanSend);
        if (plan.Before.CargoCapacity != current.Inventory.CargoCapacity)
        {
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.PersonalCargo), "personal_cargo_capacity", CanSend);
            await RestoreLocksAsync(context, current.Inventory, current.Inventory.Items.Where(item => item.Position.List == InventoryListType.PersonalCargo).Select(item => item.Position), canSend: CanSend, currentIndex: receipt.Index);
        }
        if (plan.Before.AccountCargo.Capacity != current.Inventory.AccountCargo.Capacity)
        {
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.AccountCargo), "account_cargo_capacity", CanSend);
            await RestoreLocksAsync(context, current.Inventory, current.Inventory.Items.Where(item => item.Position.List == InventoryListType.AccountCargo).Select(item => item.Position), canSend: CanSend, currentIndex: receipt.Index);
        }
        if (receipt.PreviousQuests is { } previous && !previous.SequenceEqual(current.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(current.ActiveMainQuests), "inventory_quest_conditions", CanSend);
    }

    public static async Task RestoreLocksAsync(GameConnectionContext context, InventorySnapshot inventory,
        IEnumerable<InventorySlot> slots, InventorySnapshot? before = null, Func<bool>? canSend = null,
        InventoryIndex? currentIndex = null, InventoryIndex? beforeIndex = null)
    {
        currentIndex ??= new(inventory);
        beforeIndex ??= before is null ? null : new(before);
        var locked = new List<InventorySlot>();
        foreach (var slot in slots.Distinct())
        {
            var item = currentIndex.Find(slot);
            if (item is null)
                continue; // 空槽已由物品差量清除。
            var old = beforeIndex?.Find(slot);
            var previous = beforeIndex?.FindUid(item.Uid);
            if (item.Core.SortLockFlag != 0 || old is { Core.SortLockFlag: not 0 } || previous is { Core.SortLockFlag: not 0 })
            {
                var operation = item.Core.SortLockFlag != 0 ? InventoryLockOperation.SortLock : InventoryLockOperation.SortUnlock;
                var opcode = item.Core.SortLockFlag != 0 ? CmdPacketType.REQUEST_ITEM_SORT_LOCK : CmdPacketType.REQUEST_ITEM_SORT_UNLOCK;
                await context.Reply(opcode, InventoryLockPackets.Ack(slot, operation, new(true, 0)), "restore_sort_lock", canSend);
            }
            if (item.Core.EquipmentLockId != 0)
                locked.Add(slot);
            else if (old is not null && old.Core.EquipmentLockId != 0)
                await context.Notify(NotiPacketType.ITEM_UNLOCK, InventoryLockPackets.Unlocked(slot), "clear_previous_equipment_lock", canSend);
        }
        if (locked.Count != 0)
            await context.Notify(NotiPacketType.ITEM_LOCK_LIST, InventoryLockPackets.Locked(locked), "restore_equipment_locks", canSend);
    }
}
