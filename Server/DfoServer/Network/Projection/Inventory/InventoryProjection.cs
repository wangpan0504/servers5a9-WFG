using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed partial class InventoryProjection(CharacterStatComputer stats, TownProjection town, EpicPieceCatalog pieces,
    CreatureProjection? creatures = null, SkinStorageProjection? skins = null)
{
    public Task RefreshTitleAppearanceAsync(GameConnectionContext context, Character character, Func<bool> canSend) =>
        RefreshCharacterAsync(context, character, "clone_title", canSend);

    public Task RefreshSkillTreeAsync(GameConnectionContext context, Character character, Func<bool> canSend) =>
        RefreshCharacterAsync(context, character, "skill_tree", canSend);

    public Task RefreshSkillVariantAsync(GameConnectionContext context, SkillLearningReceipt receipt, byte previous, bool includeSelf = true)
    {
        context.Session.PublishSkillVariant(receipt, previous, includeSelf);
        context.Owner.Execute(() =>
        {
            if (context.Session.CanPublishLearning(receipt) && context.Session.Phase == SessionPhase.Town &&
                CharacterSkillVariant.Read(context.Session.ReadSkillCharacter(receipt.Character)) != previous)
                town.Refresh(context);
            return true;
        });
        return Task.CompletedTask;
    }

    internal async Task RefreshCharacterAsync(GameConnectionContext context, Character character, string action, Func<bool> canSend)
    {
        character=context.Session.ReadSkillCharacter(character);
        var pvpGrade = context.Session.ReadPvpGrade(character);
        var adventureName = context.Session.ReadAdventureName();
        bool inDungeon = context.Session.Phase is SessionPhase.Dungeon or SessionPhase.Settlement;
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Actor(character, pvpGrade, context.Channel, adventureName: adventureName,
                sceneState: inDungeon ? ActorSceneState.Dungeon : ActorSceneState.Town,
                premiumPcRoom: context.Session.PremiumPcRoom, returningAdventurerType: context.Session.ReturningAdventurerType, marriage: context.MarriageAppearance), action + "_actor", canSend);
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Player(character, stats.Compute(character), context.Session.ReadAdventureLevel(), context.Channel),
            action + "_player", canSend);
        context.Owner.Execute(() =>
        {
            if (canSend() && context.Session.Phase == SessionPhase.Town && !context.BlocksTownActions)
                town.Refresh(context, adventureName: adventureName, pvpGrade: pvpGrade);
            return true;
        });
    }

    internal sealed class CashShopCapacityPublication(InventoryPlan plan)
    {
        internal bool AvatarRequired { get; } = plan.After.AvatarExpansion > plan.Before.AvatarExpansion;
        internal bool CargoRequired { get; } = plan.After.CargoCapacity > plan.Before.CargoCapacity;
        internal ushort? AvatarExpansion { get; set; }
        internal ushort? CargoWindowCapacity { get; set; }
        internal ushort? CargoListCapacity { get; set; }
        internal bool AvatarComplete { get; set; }
        internal bool CargoComplete { get; set; }
        internal bool Matches(InventorySnapshot inventory) =>
            (!AvatarRequired || AvatarComplete && AvatarExpansion == inventory.AvatarExpansion) &&
            (!CargoRequired || CargoComplete && CargoWindowCapacity == inventory.CargoCapacity && CargoListCapacity == inventory.CargoCapacity);
    }

    public async Task<bool> RefreshCashShopCapacitiesAsync(GameConnectionContext context, InventoryPlan plan,
        InventoryCommitReceipt receipt, CashShopCapacityPublication publication)
    {
        bool CanSend() => context.Session.CanPublishInventory(receipt);
        if (!CanSend()) return false;
        var current = receipt.Character.Inventory;
        // ACK64会立即重筛商城。只先安装已提交的新容量，原条目保持不变，混单奖励仍由ACK后的差量发布。
        var capacity = plan.Before with { AvatarExpansion = current.AvatarExpansion, CargoCapacity = current.CargoCapacity };
        if (publication.AvatarRequired)
        {
            if (publication.AvatarExpansion != current.AvatarExpansion)
            {
                publication.AvatarComplete = false;
                if (!await context.Sender.SendConfirmedAsync(0, (ushort)NotiPacketType.ITEM_LIST,
                    InventoryPackets.SpecialFull(capacity, InventoryListType.Avatar), "avatar_capacity_committed", CanSend)) return false;
                publication.AvatarExpansion = current.AvatarExpansion;
            }
            if (!publication.AvatarComplete)
            {
                if (!await RestoreCashShopCapacityLocksAsync(context, capacity, InventoryListType.Avatar, CanSend)) return false;
                publication.AvatarComplete = true;
            }
        }
        if (publication.CargoRequired)
        {
            if (publication.CargoWindowCapacity != current.CargoCapacity)
            {
                publication.CargoComplete = false;
                if (!await context.Sender.SendConfirmedAsync(0, (ushort)NotiPacketType.CERA_SPECIALITEM,
                    CargoPackets.PersonalCapacity(current.CargoCapacity), "personal_cargo_capacity_changed", CanSend)) return false;
                publication.CargoWindowCapacity = current.CargoCapacity;
            }
            if (publication.CargoListCapacity != current.CargoCapacity)
            {
                publication.CargoComplete = false;
                if (!await context.Sender.SendConfirmedAsync(0, (ushort)NotiPacketType.ITEM_LIST,
                    CargoPackets.Full(capacity, InventoryListType.PersonalCargo), "personal_cargo_capacity", CanSend)) return false;
                publication.CargoListCapacity = current.CargoCapacity;
            }
            if (!publication.CargoComplete)
            {
                if (!await RestoreCashShopCapacityLocksAsync(context, capacity, InventoryListType.PersonalCargo, CanSend)) return false;
                publication.CargoComplete = true;
            }
        }
        return CanSend() && publication.Matches(current);
    }

    static async Task<bool> RestoreCashShopCapacityLocksAsync(GameConnectionContext context, InventorySnapshot inventory,
        InventoryListType list, Func<bool> canSend)
    {
        // 容量全量只带原条目，此处仅正向恢复这些条目的原锁；必须实际写帧后才算完成本次容量链。
        var locked = new List<InventorySlot>();
        foreach (var item in inventory.Items.Where(item => item.Position.List == list))
        {
            if (item.Core.SortLockFlag != 0 && !await context.Sender.SendConfirmedAsync(1,
                (ushort)CmdPacketType.REQUEST_ITEM_SORT_LOCK,
                InventoryLockPackets.Ack(item.Position, InventoryLockOperation.SortLock, new(true, 0)), "restore_sort_lock", canSend))
                return false;
            if (item.Core.EquipmentLockId != 0) locked.Add(item.Position);
        }
        if (locked.Count != 0 && !await context.Sender.SendConfirmedAsync(0, (ushort)NotiPacketType.ITEM_LOCK_LIST,
            InventoryLockPackets.Locked(locked), "restore_equipment_locks", canSend)) return false;
        return canSend();
    }

    public async Task RefreshAsync(GameConnectionContext context, InventoryResult result,
        IReadOnlySet<InventorySlot>? acknowledgedSlots = null, bool refreshEquipment = true,
        InventoryListType? silentMoveList = null, bool suppressMainNotice = false, InventoryListType? suppressMoveNoticeList = null, bool suppressMailboxNotice = false,
        bool cashShopCapacitiesPublished = false)
    {
        if (!result.Accepted || result.Plan is not { Changed: true } plan)
            return;
        var receipt = result.Receipt ?? throw new InvalidOperationException("背包刷新缺少提交回执");
        bool CanSend() => context.Session.CanPublishInventory(receipt);
        if (!CanSend())
            return;
        var current = receipt.Character;
        if (!plan.Before.ShopPurchases.SequenceEqual(current.Inventory.ShopPurchases))
            await town.CashShopRestrictionsAsync(context, current, CanSend, plan.Before);
        if (plan.Before.PrivilegeService != current.Inventory.PrivilegeService)
            await PrivilegeServiceProjection.PostAsync(context, receipt);
        // CMD19只转移已有实例；登记已配置时不能用本人Player重新初始化登记，否则0267BFA0会关闭UI355。
        // 完整穿戴差量安装核心/时装孔位，Actor保留克隆来源和宠物实体，1372随后更新同UID登记位置。
        bool preserveSkillSwitch = receipt.Phase is SessionPhase.Town or SessionPhase.MapSelect &&
            plan.Reason == "move_item" && plan.EquipmentSlotFlagsAfter is null && plan.CloneTitleItemIdAfter is null &&
            (!receipt.PreviousSkillSwitchInventory.Same(SkillSwitchInventoryState.Empty) ||
             !current.SkillSwitchInventory.Same(SkillSwitchInventoryState.Empty));
        foreach (var usage in current.Inventory.ServerItems.Uses.Where(row => row.Type<=1 && !plan.Before.ServerItems.Uses.Contains(row)))
            await context.Notify(NotiPacketType.LIMIT_ITEM_USAGE,
                ServerItemUsagePackets.Changed(usage.ItemId, context.Session.ServerItemLimit(usage.Type, usage.ItemId), usage.Used),
                "server_item_usage", CanSend);
        bool mainExpansionChanged = plan.Before.Expansion != current.Inventory.Expansion;
        if (mainExpansionChanged)
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current.Inventory), "inventory_capacity_committed", CanSend);
        if (plan.Before.AvatarExpansion != current.Inventory.AvatarExpansion &&
            !(cashShopCapacitiesPublished && current.Inventory.AvatarExpansion > plan.Before.AvatarExpansion))
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
        var slots = changedSlots.Where(slot => (preserveSkillSwitch || acknowledgedSlots?.Contains(slot) != true) &&
            slot != new InventorySlot(InventoryListType.Equipment, 30) && !(inPlaceCreature && slot == creatureSlot)).ToArray();
        bool reloadPersonalCargo = plan.Reason == "sort_item" && slots.Any(slot => slot.List == InventoryListType.PersonalCargo);
        bool rebuildMoveList = silentMoveList.HasValue && changedSlots.Where(slot => slot.List == silentMoveList)
            .Select(plan.BeforeIndex.Find).Any(item => item is { Core.IsStackable: true } &&
                receipt.Index.FindUid(item.Uid)?.Core.Count != item.Core.Count);
        bool quickEquipmentMoved = silentMoveList == InventoryListType.Main && changedSlots.Any(slot =>
            QuickSlotEquipmentRules.IsQuickSlot(slot) &&
            (plan.BeforeIndex.Find(slot)?.Core.Kind == ItemKind.Equipment ||
             receipt.Index.Find(slot)?.Core.Kind == ItemKind.Equipment));
        bool creatureDetailsChanged = silentMoveList != InventoryListType.Pet &&
            changedSlots.Any(slot => !(inPlaceCreature && slot == creatureSlot) && receipt.Index.Find(slot)?.Creature is not null);
        bool reloadCreature = plan.Reason is "hatch_creature_egg" or "creature_change" or "creature_group_convert";
        if (creatureDetailsChanged && !reloadCreature)
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(current.Inventory), "creature_details_committed", CanSend);
        // 开盒确认后客户端会立即读取完整容器。仅发送增量会让结果窗读取到旧槽位，
        // 双倍或十连奖励随后显示为加载中；普通背包操作仍沿用增量协议。
        var quietReloaded = new List<InventoryListType>();
        if (plan.Reason == "magic_box_open")
        {
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current.Inventory), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.PersonalCargo), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.AccountCargo), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Avatar), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Pet), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.GuildMedal), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Talisman), "inventory_committed_full_list", CanSend);
            if (slots.Any(slot => slot.List == InventoryListType.AccountCommon))
                await context.Notify(NotiPacketType.ITEM_LIST, town.SpecialFull(context, current.Inventory, InventoryListType.AccountCommon), "inventory_committed_full_list", CanSend);
        }
        else if (plan.Reason == "rune_box_open")
        {
            // CMD27确认后客户端立即读取奖励容器：主背包补权威全量，容器46按整表重载，
            // 只发差量会留下客户端预置的奖励槽。
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current.Inventory), "inventory_committed_full_list", CanSend);
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current.Inventory, InventoryListType.Talisman), "inventory_committed_full_list", CanSend);
            if (slots.Any(slot => slot.List == InventoryListType.AccountCommon))
                await context.Notify(NotiPacketType.ITEM_LIST, town.SpecialFull(context, current.Inventory, InventoryListType.AccountCommon), "inventory_committed_full_list", CanSend);
        }
        else if (reloadPersonalCargo)
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.PersonalCargo), "personal_cargo_sorted", CanSend);
        else foreach (var list in slots.Select(slot => slot.List).Where(list => !mainExpansionChanged || list != InventoryListType.Main).Distinct().Order())
        {
            if (list == silentMoveList)
            {
                // 普通整件移动沿用ACK19；快捷栏装备还须发布已提交列表，避免本地移格后没有可见图标。
                // 用13重载保留已有物品语义；离开快捷栏的空格先清除，防止全量遗漏空格而残留旧纹章。
                if (rebuildMoveList || quickEquipmentMoved)
                {
                    var emptySlots = slots.Where(slot => slot.List == list && receipt.Index.Find(slot) is null).ToArray();
                    if (emptySlots.Length != 0)
                        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(current.Inventory, list, emptySlots, index: receipt.Index),
                            "inventory_move_empty", CanSend);
                    await context.Notify(NotiPacketType.ITEM_LIST,
                        list == InventoryListType.Main ? InventoryPackets.Full(current.Inventory)
                            : list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo ? CargoPackets.Full(current.Inventory, list)
                            : town.SpecialFull(context, current.Inventory, list),
                        "inventory_move_counts", CanSend);
                }
                continue;
            }
            if (DfoServer.Game.Titles.TitleBookService.IsBook(list))
            {
                await context.Notify(NotiPacketType.TITLE_BOOK_LIST, TitlePackets.Book(current.Inventory, (byte)list - 18), "title_book_committed", CanSend);
                continue;
            }
            // 整理，以及 Main/Avatar/Pet 穿脱替换：这些格子里已是玩家自己的物品。
            // NOTI14/1681 走 029CFDD0(...,1,1)，且 1681 不置 062CF69C，020C5630 会把每格标成新物品。
            // NOTI13 在 023BDDF0 置位该标志，新建条目改为 (...,1,0)，黄框不再出现。
            // 029CEA20 只对 0/1/7/12/38/46 做这个标记；个人仓库已在上面走 NOTI13，账号公共栏不在资格内。
            if (QuietExistingReload(plan.Reason, list, suppressMoveNoticeList, result.WholeListRefresh))
            {
                // 上架先清耗尽源槽，再重载本次变化容器：NOTI13不保证移除省略的旧槽，
                // NOTI14重发既有物品则会把全部图标标成新获得。拆堆余量由后续静默全量安装。
                // 普通穿脱仍只补清 Main 快捷栏3–8，保持既有路径不变。
                if (result.WholeListRefresh || list == InventoryListType.Main && list == suppressMoveNoticeList)
                {
                    var emptied = slots.Where(slot => slot.List == list && receipt.Index.Find(slot) is null &&
                        (result.WholeListRefresh ? plan.BeforeIndex.Find(slot) is not null : slot.Index is >= 3 and <= 8)).ToArray();
                    if (emptied.Length != 0)
                        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                            InventoryPackets.Delta(current.Inventory, list, emptied, index: receipt.Index),
                            result.WholeListRefresh ? "inventory_listing_source_cleared" : "inventory_quick_slot_cleared", CanSend);
                }
                await context.Notify(NotiPacketType.ITEM_LIST, QuietList(context, current.Inventory, list),
                    result.WholeListRefresh ? "inventory_whole_list_refresh" : "inventory_quiet_reload", CanSend);
                quietReloaded.Add(list);
                continue;
            }
            bool suppressNotice = plan.Reason == "sort_item" || suppressMainNotice && list == InventoryListType.Main ||
                list == suppressMoveNoticeList || plan.Reason == "avatar_preset_select" && list == InventoryListType.Equipment;
            // 包裹回填仍先清槽再载入，保留图标effect而不播报获得。
            // CMD19已安装的穿戴对象不能再清空；统一按容器保护，不用部位白名单遗漏宠物等槽。
            // 同模板堆叠对象的NOTI14分支不重写卡片等级，等级变化必须重建对象后再载入完整核心。
            var reloadSlots = slots.Where(slot => slot.List == list && receipt.Index.Find(slot) is { } now &&
                !(preserveSkillSwitch && slot.List == InventoryListType.Equipment) &&
                (suppressNotice ||
                    plan.BeforeIndex.Find(slot) is { } old && (old.Core.ItemId != now.Core.ItemId ||
                        old.Core.IsStackable && old.Core.EnchantUpgrade != now.Core.EnchantUpgrade))).ToArray();
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
        if (changedSlots.Any(slot => slot.List == InventoryListType.Avatar ||
                slot is { List: InventoryListType.Equipment, Index: >= 0 and <= 10 }))
        {
            var sources = ClearAvatarPackets.Sources(current.Inventory, receipt.Index);
            if (!ClearAvatarPackets.Sources(plan.Before, plan.BeforeIndex).AsSpan().SequenceEqual(sources))
                await context.Notify(NotiPacketType.SRC_AVATAR_INFO_OF_CLONE, sources, "clear_avatar_sources_committed", CanSend);
        }
        // 预设内容会因选择时重采样（PlanSelect）与当前橱窗的实时回写（SyncActive）而变化，客户端却只按
        // 缓存的 0x063D/0x063F 记录重建装扮栏预览：缺少这条单条更新，切换橱窗时纸娃娃停在旧内容。
        // 0x063F 只覆盖缓存、不重绘，不改变客户端当前浏览槽，可在任意提交后补发。
        foreach (var presetSlot in plan.Before.AvatarPresets.ContentChangedSlots(current.Inventory.AvatarPresets))
            if (current.Inventory.AvatarPresets.Find(presetSlot) is { } committedPreset)
                await context.Notify(NotiPacketType.UPDATE_AVATAR_PRESET, AvatarPresetPackets.Update(committedPreset),
                    "avatar_preset_content_committed", CanSend);
        // 护石孔里的符文（容器 46 的 210–215）不改变角色外观，也不进属性计算：客户端 0x067B/0x067C/0x06CE
        // 回执自己完成 210–212→装备键 34、213–215→装备键 35 的联动（0069A170/0069A4A0），服务端不需要补发本人
        // USERINFO。补发的 USERINFO 会走 0267C4F0→023C3A90 重灌六符文向量并触发护石面板刷新（00698B70），
        // 是图鉴符文页在按技能筛选时退回「所有技能」的唯一未排除触发路径（#747）；符文变化只发容器 46 差量。
        if (plan.ChangedSlots.Any(slot =>
                plan.BeforeIndex.Find(slot)?.Core.EchonSet is not null ||
                receipt.Index.Find(slot)?.Core.EchonSet is not null))
        {
            var oldSets = InventoryEchonEquipmentService.Records(plan.Before);
            var newSets = InventoryEchonEquipmentService.Records(current.Inventory);
            foreach (var old in oldSets.Where(old => !newSets.Any(set => set.Index == old.Index)))
                await context.Notify(NotiPacketType.CUSTOM_ABILITY_PART_SET_OPTION, EchonEquipmentPackets.Clear(current.Id, old.Index),
                    "echon_set_removed", CanSend);
            foreach (var set in newSets.Where(set => !oldSets.Contains(set)))
                await context.Notify(NotiPacketType.CUSTOM_ABILITY_PART_SET_OPTION, EchonEquipmentPackets.Set(current.Id, set),
                    "echon_set_committed", CanSend);
        }
        bool equipmentChanged = changedSlots.Any(slot => slot.List == InventoryListType.Equipment && !(inPlaceCreature && slot == creatureSlot));
        bool weaponSkinChanged = plan.Before.Skins.Selected != current.Inventory.Skins.Selected;
        bool cloneTitleChanged = plan.CloneTitleItemIdAfter is not null;
        if (skins is not null && plan.Before.Skins.SelectedPartyFrame != current.Inventory.Skins.SelectedPartyFrame)
            await skins.RefreshPartyFrameAsync(context, receipt);
        if (refreshEquipment && (equipmentChanged || weaponSkinChanged || cloneTitleChanged || plan.EquipmentSlotFlagsAfter is not null))
        {
            var skillCharacter = context.Session.ReadSkillCharacter(current);
            DfoServer.Game.Pvp.PvpDisplayGrade? pvpGrade = null;
            string? adventureName = null;
            bool appearanceChanged = cloneTitleChanged || plan.ChangedSlots.Any(slot => slot.List == InventoryListType.Equipment &&
                slot.Index is 9 or 11 or 13 or 26 or 33 && plan.BeforeIndex.Find(slot) != receipt.Index.Find(slot));
            // 称号、光环与宠物真槽/外观槽变化都刷新Actor；副本保留当前场景状态，不重新入城。
            if ((equipmentChanged || weaponSkinChanged || cloneTitleChanged) && (receipt.Phase is SessionPhase.Town or SessionPhase.MapSelect ||
                (appearanceChanged || weaponSkinChanged) && receipt.Phase is SessionPhase.Dungeon or SessionPhase.Settlement))
            {
                pvpGrade = context.Session.ReadPvpGrade(current);
                adventureName = context.Session.ReadAdventureName();
                await context.Notify(NotiPacketType.USERINFO,
                    TownPackets.Actor(skillCharacter, pvpGrade.Value, context.Channel, adventureName: adventureName,
                        sceneState: receipt.Phase is SessionPhase.Dungeon or SessionPhase.Settlement
                            ? ActorSceneState.Dungeon : ActorSceneState.Town,
                        premiumPcRoom: context.Session.PremiumPcRoom, returningAdventurerType: context.Session.ReturningAdventurerType, marriage: context.MarriageAppearance), "equipment_actor", CanSend);
            }
            // 登记配置下的纯移动由上面的完整差量和Actor完成；其它装备/开栏变更仍同步完整本人属性。
            if (equipmentChanged && !preserveSkillSwitch || cloneTitleChanged || plan.EquipmentSlotFlagsAfter is not null)
                await context.Notify(NotiPacketType.USERINFO,
                    TownPackets.Player(skillCharacter, stats.Compute(skillCharacter), context.Session.ReadAdventureLevel(), context.Channel), "equipment_player", CanSend);
            context.Owner.Execute(() =>
            {
                if ((equipmentChanged || weaponSkinChanged || cloneTitleChanged) && CanSend() && context.Session.Phase == SessionPhase.Town)
                    town.Refresh(context, adventureName: adventureName, pvpGrade: pvpGrade);
                return true;
            });
        }
        // 全量重载会重建未变化的图标，锁通知必须覆盖同一批容器中的全部实例。
        bool reloadLocks = reloadPersonalCargo || rebuildMoveList || quietReloaded.Count != 0 || mainExpansionChanged ||
            plan.Before.AvatarExpansion != current.Inventory.AvatarExpansion || plan.Reason is "magic_box_open" or "rune_box_open";
        var lockSlots = reloadLocks ? changedSlots.Concat(current.Inventory.Items.Where(item =>
            plan.Reason == "magic_box_open" && item.Position.List is (InventoryListType.Main or InventoryListType.PersonalCargo or
                InventoryListType.AccountCargo or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Talisman or InventoryListType.AccountCommon) ||
            rebuildMoveList && item.Position.List == silentMoveList ||
            quietReloaded.Contains(item.Position.List) ||
            (mainExpansionChanged || plan.Reason == "rune_box_open") && item.Position.List == InventoryListType.Main ||
            plan.Before.AvatarExpansion != current.Inventory.AvatarExpansion && item.Position.List == InventoryListType.Avatar ||
            plan.Reason == "rune_box_open" && item.Position.List == InventoryListType.Talisman ||
            reloadPersonalCargo && item.Position.List == InventoryListType.PersonalCargo).Select(item => item.Position)) : changedSlots;
        await RestoreLocksAsync(context, current.Inventory, lockSlots, plan.Before, CanSend, receipt.Index, plan.BeforeIndex);
        if (plan.ChangedCurrencies.Any(kind => kind is >= DfoServer.Game.Currency.CurrencyKind.Cera and <= DfoServer.Game.Currency.CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(current.Inventory.AccountWallet), "currency_committed_delta", CanSend);
        if (plan.AccountMaterialsChanged)
            await town.AccountMaterialsAsync(context, current, plan.Before.AccountWallet, CanSend, plan.Before);
        else if (plan.ChangedSlots.Any(slot => slot.List == InventoryListType.AccountCommon &&
                (plan.BeforeIndex.Find(slot) is null) != (receipt.Index.Find(slot) is null)) &&
            plan.Reason is not ("magic_box_open" or "rune_box_open") &&
            !(rebuildMoveList && silentMoveList == InventoryListType.AccountCommon))
            await town.SyncCommonCalendarSlotsAsync(context, plan.Before, current, CanSend);
        if (plan.ChangedCurrencies.Contains(DfoServer.Game.Currency.CurrencyKind.Mileage))
        {
            bool gained = (plan.Reason is "mileage_item" or "cash_shop_buy") && current.Inventory.AccountWallet.Mileage > plan.Before.AccountWallet.Mileage;
            await context.Notify(gained ? NotiPacketType.UPDATE_MILEAGE_DATA : NotiPacketType.MILEAGE_DATA,
                gained ? MileagePackets.Added(plan.Before.AccountWallet, current.Inventory.AccountWallet) : MileagePackets.State(current.Inventory.AccountWallet),
                "mileage_committed_delta", CanSend);
        }
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
                await context.Notify(NotiPacketType.PREMIUM_INFO_LIST,
                    PremiumPackets.SelectEntries(current.Inventory.AccountWallet, premiumPcRoom: context.Session.PremiumPcRoom), "premium_info_committed", CanSend);
                if (plan.Before.AccountWallet.ContractExpiry(27) != current.Inventory.AccountWallet.ContractExpiry(27))
                    await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(current.Learning.ExpertContractExpiresAt,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "expert_contract_committed", CanSend);
            }
        }
        if (!plan.Before.DevilUses.SequenceEqual(current.Inventory.DevilUses) ||
            Enumerable.Range(0, 8).Any(slot => DevilContractPolicy.Expiry(plan.Before.AccountWallet, slot) !=
                DevilContractPolicy.Expiry(current.Inventory.AccountWallet, slot)))
            await DungeonCardMembershipProjection.RefreshAsync(context, includeOverview: false);
        if (!suppressMailboxNotice && !plan.OverflowItems.IsEmpty && CanSend())
            context.Session.NotifyMailbox(receipt.Character.Id, restoreOnTown: true);
        if (plan.Before.CargoCapacity != current.Inventory.CargoCapacity &&
            !(cashShopCapacitiesPublished && current.Inventory.CargoCapacity > plan.Before.CargoCapacity))
            await RefreshPersonalCargoCapacityAsync(context, current.Inventory, plan.Before.CargoCapacity, receipt.Index, CanSend);
        if (plan.Before.AccountCargo.Capacity != current.Inventory.AccountCargo.Capacity)
        {
            await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current.Inventory, InventoryListType.AccountCargo), "account_cargo_capacity", CanSend);
            await RestoreLocksAsync(context, current.Inventory, current.Inventory.Items.Where(item => item.Position.List == InventoryListType.AccountCargo).Select(item => item.Position), canSend: CanSend, currentIndex: receipt.Index);
        }
        if (receipt.PreviousQuests is { } previous && !previous.SequenceEqual(current.ActiveMainQuests))
            await QuestProjection.ProgressAsync(context,previous,current,CanSend,"inventory_quest_conditions");
        await SkillSwitchInventoryProjection.RefreshInventoryAsync(context, receipt, plan.Before);

        static bool QuietExistingReload(string reason, InventoryListType list, InventoryListType? suppressMoveNoticeList,
            bool wholeListRefresh) =>
            (wholeListRefresh || reason == "sort_item" || list == suppressMoveNoticeList ||
                reason == "avatar_preset_select" && list == InventoryListType.Avatar) &&
            list is InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or
                InventoryListType.AccountCargo or InventoryListType.GuildMedal or InventoryListType.Talisman;
        byte[] QuietList(GameConnectionContext connection, InventorySnapshot inventory, InventoryListType list) => list switch
        {
            InventoryListType.Main => InventoryPackets.Full(inventory),
            InventoryListType.AccountCargo => CargoPackets.Full(inventory, list),
            _ => town.SpecialFull(connection, inventory, list)
        };
        if (creatures is not null && !inPlaceCreature && receipt.Phase == SessionPhase.Town && changedSlots.Contains(creatureSlot))
            await creatures.QuestsChangedAsync(context, plan.Before, current, CanSend);
    }

    static async Task RefreshPersonalCargoCapacityAsync(GameConnectionContext context, InventorySnapshot inventory,
        ushort previousCapacity, InventoryIndex index, Func<bool> canSend)
    {
        // 66先更新打开的金库窗口；13先写入容量会让原生的“容量增加”判断跳过重绘。
        if (inventory.CargoCapacity > previousCapacity)
            await context.Notify(NotiPacketType.CERA_SPECIALITEM, CargoPackets.PersonalCapacity(inventory.CargoCapacity),
                "personal_cargo_capacity_changed", canSend);
        await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(inventory, InventoryListType.PersonalCargo),
            "personal_cargo_capacity", canSend);
        await RestoreLocksAsync(context, inventory, inventory.Items.Where(item => item.Position.List == InventoryListType.PersonalCargo)
            .Select(item => item.Position), canSend: canSend, currentIndex: index);
    }

    // 拒绝路径没有提交计划，客户端却已先移除所选材料；按当前已提交快照整列重发该容器。
    // 守卫与提交刷新同一套：选角代数、阶段或角色实例变化后，迟到请求不再发布。
    public async Task RefreshListAsync(GameConnectionContext context, InventoryListType list, string action)
    {
        var (character, epoch, phase) = context.Owner.Execute(() =>
            (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        if (character is null)
            return;
        bool CanSend() => context.Owner.Execute(() => context.Session.SelectionEpoch == epoch &&
            context.Session.Phase == phase && ReferenceEquals(context.Session.Selected, character));
        if (!CanSend())
            return;
        await context.Notify(NotiPacketType.ITEM_LIST, town.SpecialFull(context, character.Inventory, list), action, CanSend);
        await RestoreLocksAsync(context, character.Inventory,
            character.Inventory.Items.Where(item => item.Position.List == list).Select(item => item.Position), canSend: CanSend);
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
                bool visible = item.Core.SortLockFlag != 0 && slot.List != InventoryListType.Equipment;
                var operation = visible ? InventoryLockOperation.SortLock : InventoryLockOperation.SortUnlock;
                var opcode = visible ? CmdPacketType.REQUEST_ITEM_SORT_LOCK : CmdPacketType.REQUEST_ITEM_SORT_UNLOCK;
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
