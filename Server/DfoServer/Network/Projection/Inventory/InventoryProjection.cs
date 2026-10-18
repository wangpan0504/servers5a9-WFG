using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed class InventoryProjection(CharacterStatComputer stats, TownProjection town, EpicPieceCatalog pieces,
    CreatureProjection? creatures = null)
{
    public async Task RefreshAsync(GameConnectionContext context, InventoryResult result,
        IReadOnlySet<InventorySlot>? acknowledgedSlots = null, bool refreshEquipment = true, bool silentMainMove = false,
        bool suppressMainNotice = false)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan)
            return;
        var receipt = result.Receipt ?? throw new InvalidOperationException("背包刷新缺少提交回执");
        bool CanSend() => context.Session.CanPublishInventory(receipt);
        if (!CanSend())
            return;
        var current = receipt.Character;
        if (plan.Before.AvatarExpansion != current.Inventory.AvatarExpansion)
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Avatar), "avatar_capacity_committed", CanSend);
        var creatureSlot = DfoServer.Game.Creatures.CreatureRules.EquippedSlot;
        bool inPlaceCreature = creatures is not null && plan.BeforeIndex.Find(creatureSlot) is { Creature: not null } oldCreature &&
            receipt.Index.Find(creatureSlot) is { Creature: not null } newCreature && oldCreature.Uid == newCreature.Uid &&
            oldCreature.Core with { ItemId = newCreature.Core.ItemId, SealFlag = newCreature.Core.SealFlag } == newCreature.Core &&
            (oldCreature.Creature != newCreature.Creature || oldCreature.Core.ItemId != newCreature.Core.ItemId);
        if (inPlaceCreature) await creatures!.ChangedAsync(context, result);
        bool seriaLuckChanged = plan.Before.AccountWallet.SeriaLuck != current.Inventory.AccountWallet.SeriaLuck;
        var changedSlots = plan.ChangedSlots.Concat(plan.ChangedCurrencies.Select(ItemSlotBoundService.CurrencySlot).Where(slot => slot.HasValue)
            .Select(slot => new InventorySlot(InventoryListType.Main, slot!.Value))).Distinct().ToArray();
        if (result.WholeListRefresh)
        {
            // 整列重建：在**变化槽**（含要清空的源槽）之外，把该容器当前实际占用的每个槽与 Main 的虚拟货币槽
            // 也一起发一遍，强制客户端丢弃并重新装载整个容器。这是解除「源槽仍被业务窗口持有」这类残留状态的
            // 唯一可靠方式（拍卖行上架后背包不可操作就是这一种，见 InventoryResult.WholeListRefresh）。
            // 与登录全量同一套编码：清空条目为目标 slot + itemId=-1，因此解引用走既有 Delta 语义，不手搓包体。
            changedSlots = changedSlots
                .Concat(current.Inventory.Items.Where(item => item.Position.List is InventoryListType.Main or InventoryListType.Avatar or
                    InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.PersonalCargo).Select(item => item.Position))
                .Concat(CurrencyPackets.VirtualSlots.Select(slot => new InventorySlot(InventoryListType.Main, slot)))
                .Distinct().ToArray();
        }
        var slots = changedSlots.Where(slot => acknowledgedSlots?.Contains(slot) != true &&
            slot != new InventorySlot(InventoryListType.Equipment, 30) && !(inPlaceCreature && slot == creatureSlot)).ToArray();
        bool rebuildMain = silentMainMove && changedSlots.Where(slot => slot.List == InventoryListType.Main)
            .Select(plan.BeforeIndex.Find).Any(item => item is { Core.IsStackable: true } &&
                receipt.Index.FindUid(item.Uid)?.Core.Count != item.Core.Count);
        bool creatureDetailsChanged = changedSlots.Any(slot => !(inPlaceCreature && slot == creatureSlot) && receipt.Index.Find(slot)?.Creature is not null);
        bool reloadCreature = plan.Reason is "hatch_creature_egg" or "creature_change" or "creature_group_convert";
        if (creatureDetailsChanged && !reloadCreature)
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(current.Inventory), "creature_details_committed", CanSend);
        // 开盒确认后客户端会立即读取完整容器。仅发送增量会让结果窗读取到旧槽位，
        // 双倍或十连奖励随后显示为加载中；普通背包操作仍沿用增量协议。
        if (plan.Reason == "magic_box_open")
        {
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current.Inventory), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.PersonalCargo), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.AccountCargo), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Avatar), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Pet), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.GuildMedal), "inventory_committed_full_list", CanSend);
        }
        else foreach (var list in slots.Select(slot => slot.List).Distinct().Order())
        {
            if (silentMainMove && list == InventoryListType.Main)
            {
                // ACK19已移动整件；数量变化才用13载入。1681仍会执行STK的新物品图标标记，不能代替无动画同步。
                if (rebuildMain)
                {
                    var emptySlots = slots.Where(slot => slot.List == InventoryListType.Main && receipt.Index.Find(slot) is null).ToArray();
                    if (emptySlots.Length != 0)
                        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(current.Inventory, list, emptySlots, index: receipt.Index),
                            "inventory_main_move_empty", CanSend);
                    await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current.Inventory), "inventory_main_move_counts", CanSend);
                }
                continue;
            }
            if (DfoServer.Game.Titles.TitleBookService.IsBook(list))
            {
                await context.Notify(NotiPacketType.TITLE_BOOK_LIST, TitlePackets.Book(current.Inventory, (byte)list - 18), "title_book_committed", CanSend);
                continue;
            }
            bool suppressNotice = plan.Reason == "sort_item" || suppressMainNotice && list == InventoryListType.Main;
            // 整理和ACK19回填后的同模板增量只更新属性；先清槽再以1681载入，保留图标effect而不播报获得。
            var reloadSlots = slots.Where(slot => slot.List == list && receipt.Index.Find(slot) is { } now &&
                (suppressNotice ||
                    plan.BeforeIndex.Find(slot) is { } old && old.Core.ItemId != now.Core.ItemId)).ToArray();
            var updateType = suppressNotice ?
                NotiPacketType.UPDATE_ITEM_LIST_NOT_NOTICE : NotiPacketType.UPDATE_ITEM_LIST;
            if (reloadSlots.Length != 0)
                await context.Notify(updateType, InventoryPackets.Delta(InventorySnapshot.Empty, list, reloadSlots), "inventory_clear_replaced", CanSend);
            // 孵化和定向转换先清旧模板，再提交完整详情，最后让新宠物引用已到达的详情。
            if (reloadCreature && creatureDetailsChanged && list == InventoryListType.Pet)
                await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(current.Inventory), "creature_details_committed", CanSend);
            await context.Notify(updateType, InventoryPackets.Delta(current.Inventory, list, slots, index: receipt.Index),
                result.WholeListRefresh ? "inventory_whole_list_refresh" : "inventory_committed_delta", CanSend);
        }
        if (!DailyBoosterUsage.Same(plan.Before.DailyBoosterUses, current.Inventory.DailyBoosterUses) && context.BoosterUses is { } boosterUses)
            await boosterUses.CommittedAsync(receipt);
        bool equipmentChanged = changedSlots.Any(slot => slot.List == InventoryListType.Equipment && !(inPlaceCreature && slot == creatureSlot));
        if (refreshEquipment && (equipmentChanged || plan.EquipmentSlotFlagsAfter is not null))
        {
            bool appearanceChanged = plan.ChangedSlots.Any(slot => slot.List == InventoryListType.Equipment &&
                slot.Index is 9 or 11 or 26 or 33 && plan.BeforeIndex.Find(slot) != receipt.Index.Find(slot));
            // 光环与宠物真槽/外观槽变化都刷新同屏Actor；副本沿用真实场景状态和既有过房Ready流程。
            if (equipmentChanged && (receipt.Phase is SessionPhase.Town or SessionPhase.MapSelect ||
                appearanceChanged && receipt.Phase is SessionPhase.Dungeon or SessionPhase.Settlement))
                await context.Notify(NotiPacketType.USERINFO,
                    TownPackets.Actor(current, context.Channel, adventureName: context.Session.ReadAdventureName(),
                        sceneState: receipt.Phase is SessionPhase.Dungeon or SessionPhase.Settlement
                            ? ActorSceneState.Dungeon : ActorSceneState.Town,
                        premiumPcRoom: context.Session.PremiumPcRoom), "equipment_actor", CanSend);
            await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(current, stats.Compute(current), context.Channel), "equipment_player", CanSend);
            context.Owner.Execute(() =>
            {
                if (equipmentChanged && CanSend() && context.Session.Phase == SessionPhase.Town)
                    town.Refresh(context);
                return true;
            });
        }
        var lockSlots = rebuildMain ? changedSlots.Concat(current.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main)
            .Select(item => item.Position)) : changedSlots;
        await RestoreLocksAsync(context, current.Inventory, lockSlots, plan.Before, CanSend, receipt.Index, plan.BeforeIndex);
        if (plan.ChangedCurrencies.Any(kind => kind is >= DfoServer.Game.Currency.CurrencyKind.Cera and <= DfoServer.Game.Currency.CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(current.Inventory.AccountWallet), "currency_committed_delta", CanSend);
        if (plan.ChangedEpicPieces.Length != 0)
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(current.Inventory.EpicPieces, pieces, plan.ChangedEpicPieces), "epic_book_committed_delta", CanSend);
        if (seriaLuckChanged)
            await context.Notify(NotiPacketType.BOOSTER_GAGE, SeriaLuckPackets.Gauge(plan.Before.AccountWallet.SeriaLuck,
                current.Inventory.AccountWallet.SeriaLuck), "seria_luck_gauge_committed", CanSend);
        if (!plan.Before.AccountWallet.PremiumContracts.SequenceEqual(current.Inventory.AccountWallet.PremiumContracts))
        {
            if (context.Premiums is { } premiums) await premiums.ContractsAsync(current, guard: CanSend);
            else
            {
                await context.Notify(NotiPacketType.PREMIUM_INFO_LIST, PremiumPackets.SelectEntries(current.Inventory.AccountWallet), "premium_info_committed", CanSend);
                if (plan.Before.AccountWallet.ContractExpiry(27) != current.Inventory.AccountWallet.ContractExpiry(27))
                    await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(current.Learning.ExpertContractExpiresAt,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "expert_contract_committed", CanSend);
            }
        }
        if (!plan.Before.DevilUses.SequenceEqual(current.Inventory.DevilUses) ||
            Enumerable.Range(0, 8).Any(slot => DevilContractPolicy.Expiry(plan.Before.AccountWallet, slot) !=
                DevilContractPolicy.Expiry(current.Inventory.AccountWallet, slot)))
            await DungeonCardMembershipProjection.RefreshAsync(context, includeOverview: false);
        if (!plan.OverflowItems.IsEmpty)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "overflow_mail_committed", CanSend);
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
        await SkillSwitchInventoryProjection.RefreshInventoryAsync(context, receipt, plan.Before);
        if (creatures is not null && !inPlaceCreature && receipt.Phase == SessionPhase.Town && changedSlots.Contains(creatureSlot))
            await creatures.QuestsChangedAsync(context, plan.Before, current, CanSend);
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
            if (DfoServer.Game.Titles.TitleBookService.IsBook(slot.List)) continue;
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
