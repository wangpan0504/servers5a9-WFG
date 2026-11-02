using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryMoveService
{
    // ACK19 的 23 显示“无法移动该物品”；1 是地区限制，不能用于普通物品校验失败。
    const ushort CannotMoveItem = 23;
    // 扩展装备栏标志：槽22辅助装备=1、槽23魔法石=2、槽25耳环=16（客户端 02B64FA0 同一位定义）。
    internal const byte SupportSlotFlag = 1, MagicStoneSlotFlag = 2, EarringSlotFlag = 16;
    internal const byte ExpansionSlotFlags = SupportSlotFlag | MagicStoneSlotFlag | EarringSlotFlag;

    internal const string LoginWearRestatementReason = "move_login_wear_restatement";

    public static InventoryResult Plan(Character character, InventoryMoveRequest request, ItemCatalog catalog, long utcSeconds,
        PremiumContractCatalog? contracts = null, bool loginWearRestatement = false)
    {
        if (InventoryKnightShieldService.Recognizes(request) || character.Inventory.ShieldDeck.Items[0] != 0 &&
            (request.Source == InventoryKnightShieldService.MainSlot || request.Destination == InventoryKnightShieldService.MainSlot))
            return InventoryResult.Reject(CannotMoveItem);
        if (request.Source == new InventorySlot(InventoryListType.Equipment, 30) ||
            request.Destination == new InventorySlot(InventoryListType.Equipment, 30))
            return InventoryResult.Reject(CannotMoveItem);
        // 装载符文只能走有目标身份校验的装卸用例，普通移格不能把整堆放进一个孔。
        if (IsEquippedRune(request.Source) || IsEquippedRune(request.Destination))
            return InventoryResult.Reject(CannotMoveItem);
        if (request.Source.List is >= InventoryListType.TitleGeneral and <= InventoryListType.TitleEvent ||
            request.Destination.List is >= InventoryListType.TitleGeneral and <= InventoryListType.TitleEvent)
            return InventoryResult.Reject(CannotMoveItem);
        // 调用方在owner内取得稳定输入；复制技能数组，后续资格读取和前提使用同一份快照。
        character = character with { Skills = [.. character.Skills] };
        long? qualificationExpiresAt = null;
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var destination = before.Find(request.Destination);
        // 029E10D0跨容器发送器仍从源容器读取目标格的模板；该回显不代表目标仓库实际占用。
        bool sourceContainerEcho = request.Source.List != request.Destination.List &&
            (IsCargo(request.Source.List) || IsCargo(request.Destination.List)) &&
            before.Find(new(request.Source.List, request.Destination.Index)) is { } echoed &&
            echoed.Core.ItemId == request.DestinationItemId;
        // 当前客户端把个人/账号金库格号写进物品编号：空格存入、取出，以及丢到已占用格（编号为0或格号）。
        // 0 仍是空格哨兵，不能把 0 号格的真实物品当成别名。已占用格只在编号对不上模板时改投空格，避免盖掉原物品。
        bool sourceMatches = source is not null && CargoCell(request.Source, request.SourceItemId) ||
            (source?.Core.ItemId ?? 0) == request.SourceItemId;
        // 实收装扮换装后的下一次穿戴会把仍有实例的目标回显为0。仅有真实源装扮时兼容，
        // 实际占用仍取权威快照，沿后续双向资格校验和原子交换保留两件实例；非零错误编号仍拒绝。
        bool avatarEmptyTargetEcho = request.Source.List == InventoryListType.Avatar &&
            request.Destination is { List: InventoryListType.Equipment, Index: >= 0 and <= 11 } &&
            request.Count == 1 && request.DestinationItemId == 0 &&
            source is { Core.Kind: ItemKind.Avatar, Avatar: not null } &&
            destination is { Core.Kind: ItemKind.Avatar, Avatar: not null };
        bool destinationMatches = destination is null && (CargoCell(request.Destination, request.DestinationItemId) || sourceContainerEcho) ||
            (destination?.Core.ItemId ?? 0) == request.DestinationItemId || avatarEmptyTargetEcho ||
            destination is not null && IsCargo(request.Destination.List) &&
            (request.DestinationItemId == 0 || CargoCell(request.Destination, request.DestinationItemId) || sourceContainerEcho);
        bool throwOntoOccupied = source is not null && destination is not null && destinationMatches &&
            IsCargo(request.Destination.List) &&
            destination.Core.ItemId != request.DestinationItemId &&
            (request.DestinationItemId == 0 || CargoCell(request.Destination, request.DestinationItemId) || sourceContainerEcho);
        // 空源格与真实卸装无法区分，必须执行真实移动，不能按入城时间吞掉玩家操作。
        // 只保留实际源格已占用、源编号却为0的登录复述兼容；这种请求无法合法卸入该格。
        if (loginWearRestatement && source is not null && IsLoginWearRestatement(request, destination))
            return new(true, 0, new(before, before, LoginWearRestatementReason));
        if (request.Actor is not (-1 or 0) || request.Flag0 != 0 || request.Flag1 != 0 || request.DestinationCount != 0 ||
            !sourceMatches || !destinationMatches)
            return InventoryResult.Reject(CannotMoveItem);
        // 029DA4CE/029DA611 写入模板编号，空格写0；先按原顺序校验，避免重复穿戴请求被反向解释为卸装。
        var sourceSlot = request.Source;
        var destinationSlot = request.Destination;
        if (source is null)
        {
            (source, destination) = (destination, source);
            (sourceSlot, destinationSlot) = (destinationSlot, sourceSlot);
        }
        // ACK19 的 23 是「无法移动该物品」。过期实例仍可离开穿戴栏（卸下/替换），
        // 与契约到期卸装同一口径；背包内过期物不能移动或重新穿戴。
        if (source is null || ExpiredMoveBlocked(source, sourceSlot, destinationSlot, utcSeconds) ||
            destination is not null && ExpiredMoveBlocked(destination, destinationSlot, sourceSlot, utcSeconds))
            return InventoryResult.Reject(CannotMoveItem);
        var storedSource = source;
        // 旧公会币在仓库及迁移空间不足时保留原分类；只归一本次移出的数量，剩余实例仍按原仓库合同保存。
        if (source.Core.ItemId == GameWorld.Guilds.GuildNpcCatalog.MemberCoinItem && source.Core.Kind == ItemKind.Material)
        {
            var sourceTemplate = catalog.Get(source.Core.ItemId);
            if (InventoryKindCompatibility.AcceptsKind(source.Core.Kind, sourceTemplate))
                source = source with { Core = InventoryKindCompatibility.Normalize(source.Core, sourceTemplate) };
        }
        if (throwOntoOccupied && sourceSlot.List != destinationSlot.List &&
            !(source.Core.IsStackable && destination!.Core.IsStackable && source.Core.ItemId == destination.Core.ItemId))
        {
            var emptyCargo = FindEmptyDestination(character, source.Core, destinationSlot.List, catalog, utcSeconds, contracts);
            if (emptyCargo is null)
                return InventoryResult.Reject(CannotMoveItem);
            destinationSlot = emptyCargo.Value;
            destination = null;
        }
        bool movesCreatureSkin = IsCreatureSkin(sourceSlot) || IsCreatureSkin(destinationSlot);
        if (movesCreatureSkin)
        {
            if (!ValidCreatureSkinItem(source) || destination is not null && !ValidCreatureSkinItem(destination))
                return InventoryResult.Reject(CannotMoveItem);
            // 幻化仍是原宠物实例；正反交换均冻结实际详情期限，不让过期前的计划越期提交。
            foreach (var item in new[] { source, destination })
                if (item is { ExpireTime: > 0 })
                    qualificationExpiresAt = qualificationExpiresAt is null ? item.ExpireTime : Math.Min(qualificationExpiresAt.Value, item.ExpireTime);
        }
        int count = InventoryStackRuleService.NormalizeMoveCount(source.Core, request.Count);
        if (count <= 0 || !AllowsAt(destinationSlot, source.Core) ||
            sourceSlot.List != destinationSlot.List && !InventoryCargoService.AllowsInsert(source.Core, destinationSlot.List, catalog.Get(source.Core.ItemId)))
            return InventoryResult.Reject(CannotMoveItem);
        if (sourceSlot == destinationSlot)
            return new(true, 0, new(before, before, "move_no_change", Precondition()));
        if (source.Core.IsStackable && sourceSlot.List != destinationSlot.List)
        {
            // 跨容器存取优先完整合并，不能合并时仍允许空格；同容器手动拆分不走自动选格。
            int limit = catalog.Get(source.Core.ItemId).StackLimit;
            var mergeTarget = before.Items.Where(item => item.Position.List == destinationSlot.List &&
                InventoryStackRuleService.CanMerge(source.Core, item.Core, count, limit) &&
                AllowsAt(item.Position, source.Core)).MinBy(item => item.Position.Index);
            if (mergeTarget is not null)
            {
                destinationSlot = mergeTarget.Position;
                destination = mergeTarget;
            }
            else if (destination is not null)
            {
                var empty = FindEmptyDestination(character, source.Core, destinationSlot.List, catalog, utcSeconds, contracts);
                if (empty is null)
                    return InventoryResult.Reject(CannotMoveItem);
                destinationSlot = empty.Value;
                destination = null;
            }
        }
        // 孔位绑定装备槽而不跟随护石实例：带已装符文的护石离开 34/35 会把符文留在原槽错挂到别的护石上。
        // 因此必须先卸符文再卸护石，两槽互换同样按卸下处理。
        if (VacatesOccupiedSockets(sourceSlot, destinationSlot) || VacatesOccupiedSockets(destinationSlot, sourceSlot))
            return InventoryResult.Reject(CannotMoveItem);
        var planned = before.Items.ToDictionary(item => item.Position);
        var moving = PrepareForDestination(source, destinationSlot, destination, catalog);
        if (destination is not null && source.Core.IsStackable && destination.Core.IsStackable && source.Core.ItemId == destination.Core.ItemId)
        {
            // 同模板拖放只能完整合并本次数量，不能偷偷少移动或退化为交换。
            if (!InventoryStackRuleService.CanMerge(source.Core, destination.Core, count, catalog.Get(source.Core.ItemId).StackLimit))
                return InventoryResult.Reject(CannotMoveItem);
            planned[destinationSlot] = destination with { Core = destination.Core with { Value = checked(destination.Core.Value + count) } };
            DecreaseSource();
        }
        else if (destination is not null)
        {
            if (count != source.Core.Count || !AllowsAt(sourceSlot, destination.Core) ||
                sourceSlot.List != destinationSlot.List && !InventoryCargoService.AllowsInsert(destination.Core, sourceSlot.List, catalog.Get(destination.Core.ItemId)))
                return InventoryResult.Reject(CannotMoveItem);
            planned[sourceSlot] = PrepareForDestination(destination, sourceSlot, source, catalog);
            planned[destinationSlot] = moving;
            count = 1;
        }
        else
        {
            if (source.Core.IsStackable && count < source.Core.Count)
            {
                if (source.Core.EquipmentLockId != 0)
                    return InventoryResult.Reject(CannotMoveItem);
                moving = moving with { Uid = 0, Core = moving.Core with { Value = count } };
            }
            planned[destinationSlot] = moving;
            DecreaseSource();
        }

        var after = before with { Items = planned.Values.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        // 自动合并必须按实际移动方向回复；其他移动（尤其穿脱）仍保持原请求顺序。
        bool forward = sourceSlot == request.Source || source.Core.IsStackable && sourceSlot.List != destinationSlot.List;
        return new(true, 0, new(before, after, "move_item", Precondition()), count,
            forward ? sourceSlot : destinationSlot, forward ? destinationSlot : sourceSlot);

        InventoryCharacterPrecondition Precondition() => new(character, qualificationExpiresAt);

        bool ValidCreatureSkinItem(InventoryItem item)
        {
            if (item.Uid <= 0 || item.Uid > int.MaxValue || item.Core.Value != item.Uid ||
                !CreatureSkinTemplate(item.Core, catalog.Get(item.Core.ItemId)))
                return false;
            try
            {
                item.Core.Validate();
                item.ValidateDetails(allocating: false);
                return true;
            }
            catch (InvalidDataException) { return false; }
        }

        bool AllowsAt(InventorySlot position, ItemCore core)
        {
            // 源槽作为 ignore：护石在两槽之间互换时，另一槽的占用者就是本次被移走的物品。
            if (!Allows(character, position, core, catalog, utcSeconds, contracts, sourceSlot))
                return false;
            // 只有本次实际穿戴依赖霸王等级加成时冻结期限；卸装、普通移格和原等级合格不受其到期限制。
            if (position.List == InventoryListType.Equipment && position.Index != InventoryCreatureSkinService.AuraTargetSlot &&
                core.Kind is (ItemKind.Equipment or ItemKind.Avatar) &&
                character.Level < catalog.Get(core.ItemId).MinimumLevel + (core.Kind == ItemKind.Equipment ? unchecked((sbyte)core.EmancipateLevel) : 0))
                qualificationExpiresAt = character.Inventory.AccountWallet.ContractExpiry(22);
            return true;
        }

        // 护石槽 vacated 时仍留有任一枚已装符文：三孔按装备槽连续编号，符文本体留在原槽。
        bool VacatesOccupiedSockets(InventorySlot from, InventorySlot to) =>
            from != to && from.List == InventoryListType.Equipment &&
            from.Index is ItemSlotBoundService.TalismanEquipmentStart or ItemSlotBoundService.TalismanEquipmentEnd &&
            Enumerable.Range(0, 3).Any(offset => before.Find(new(InventoryListType.Talisman,
                (short)(ItemSlotBoundService.EquippedRuneStart +
                    (from.Index - ItemSlotBoundService.TalismanEquipmentStart) * 3 + offset))) is not null);

        void DecreaseSource()
        {
            if (count == source.Core.Count)
                planned.Remove(sourceSlot);
            else
                planned[sourceSlot] = storedSource with { Core = storedSource.Core with { Value = storedSource.Core.Value - count } };
        }
    }

    static bool IsCargo(InventoryListType list) => list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;

    static bool IsEquippedRune(InventorySlot slot) => slot.List == InventoryListType.Talisman &&
        slot.Index is >= ItemSlotBoundService.EquippedRuneStart and <= ItemSlotBoundService.EquippedRuneEnd;

    // 复述候选：Main源编号0、数量1 → 已穿装备槽12–25；还须由调用方确认源格实际被占用。
    static bool IsLoginWearRestatement(InventoryMoveRequest request, InventoryItem? destination) =>
        request.SourceItemId == 0 && request.Count == 1 && request.DestinationCount == 0 &&
        request.Actor is -1 or 0 && request.Flag0 == 0 && request.Flag1 == 0 &&
        request.Source.List == InventoryListType.Main && request.Destination.List == InventoryListType.Equipment &&
        request.Destination.Index is >= 12 and <= 25 &&
        destination is not null && destination.Core.ItemId == request.DestinationItemId;

    static bool CargoCell(InventorySlot slot, int requestedId) =>
        IsCargo(slot.List) && slot.Index > 0 && requestedId == slot.Index;

    static InventorySlot? FindEmptyDestination(Character character, ItemCore core, InventoryListType list, ItemCatalog catalog, long utcSeconds,
        PremiumContractCatalog? contracts)
    {
        var inventory = character.Inventory;
        var range = ItemSlotBoundService.Range(core.Kind, inventory);
        if (list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo)
            range = (0, (short)((list == InventoryListType.PersonalCargo ? inventory.CargoCapacity : inventory.AccountCargo.Capacity) - 1));
        else if (list == InventoryListType.Main)
            range = (3, ItemSlotBoundService.List(core.Kind) == list ? range.End : (short)8);
        for (short slot = range.Start; slot <= range.End; slot++)
        {
            var position = new InventorySlot(list, slot);
            if (inventory.Find(position) is null && Allows(character, position, core, catalog, utcSeconds, contracts))
                return position;
        }
        return null;
    }

    public static bool Allows(Character character, InventorySlot position, ItemCore core, ItemCatalog catalog, long? utcSeconds = null,
        PremiumContractCatalog? contracts = null, InventorySlot? ignore = null)
    {
        if (!ItemSlotBoundService.Allows(character.Inventory, position, core.Kind))
            return false;
        if (position.List != InventoryListType.Equipment)
            return true;
        var template = catalog.Get(core.ItemId);
        if (template.PvpFreeEquipment && !character.IsPvpPrivate) return false;
        if (core.Kind == ItemKind.Talisman)
        {
            if (position.Index is not (ItemSlotBoundService.TalismanEquipmentStart or ItemSlotBoundService.TalismanEquipmentEnd) ||
                catalog.Talisman(core.ItemId)?.AllowsUse(character.Level, character.Job, character.GrowType) != true)
                return false;
            // 官方规则：两个护石槽不能同时装备同一技能的两个护石。两槽互换时另一槽的占用者
            // 正是本次移动的源物品，由 ignore 排除，不算重复装备。
            var other = new InventorySlot(InventoryListType.Equipment,
                (short)(ItemSlotBoundService.TalismanEquipmentStart + ItemSlotBoundService.TalismanEquipmentEnd - position.Index));
            return other == ignore || character.Inventory.Find(other) is not { } worn ||
                catalog.Talisman(worn.Core.ItemId)?.SkillIndex is not { } wornSkill ||
                catalog.Talisman(core.ItemId)?.SkillIndex is not { } movingSkill ||
                wornSkill != movingSkill;
        }
        if (IsCreatureSkin(position))
            return (character.EquipmentSlotFlags & InventoryCreatureSkinService.UnlockFlag) != 0 && CreatureSkinTemplate(core, template);
        if (position.Index == InventoryCreatureSkinService.AuraTargetSlot)
            return (character.EquipmentSlotFlags & InventoryCreatureSkinService.AuraUnlockFlag) != 0 &&
                core.Kind == ItemKind.Avatar && template.Id == core.ItemId && template.EquipmentSlot is 9 or 11;
        int unlock = position.Index switch { 22 => SupportSlotFlag, 23 => MagicStoneSlotFlag, 25 => EarringSlotFlag, _ => 0 };
        if (unlock != 0 && (character.EquipmentSlotFlags & unlock) == 0) return false;
        // 女鬼剑流浪武士的副武器栏接受普通武器；觉醒位不改变基础转职。
        bool matchesSlot = template.EquipmentSlot == position.Index ||
            core.Kind == ItemKind.Avatar && template.EquipmentSlot == 9 && position.Index == 11 ||
            template.EquipmentSlot == 12 && position.Index == 24 && character.Job == 11 && (character.GrowType & 0x0f) == 4;
        // 宠物容器先核对物品部位；角色等级、职业和技能不能代替宠物成长系统的规则。
        if (core.Kind is ItemKind.Creature or ItemKind.CreatureEquipment)
            return matchesSlot;
        return matchesSlot && WearLevelSatisfied(character, template, utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), contracts, core) &&
            (template.Jobs & (1 << character.Job)) != 0 &&
            RequiredSkillSatisfied(character, template);
    }

    static bool ExpiredMoveBlocked(InventoryItem item, InventorySlot from, InventorySlot to, long utcSeconds) =>
        item.IsExpired(utcSeconds) && (from.List != InventoryListType.Equipment || to.List == InventoryListType.Equipment);

    static bool IsCreatureSkin(InventorySlot position) => position.List == InventoryListType.Equipment &&
        position.Index == InventoryCreatureSkinService.TargetSlot;

    static bool CreatureSkinTemplate(ItemCore core, ItemTemplate template) => core.Kind == ItemKind.Creature && core.SealFlag == 0 &&
        template.Id == core.ItemId && template is { Kind: ItemKind.Creature, EquipmentSlot: 26, CreatureSubType: 0 };

    internal static bool WearLevelSatisfied(Character character, ItemTemplate template, long utcSeconds,
        PremiumContractCatalog? contracts = null, ItemCore? core = null)
    {
        int required = template.MinimumLevel + (core?.Kind == ItemKind.Equipment ? unchecked((sbyte)core.EmancipateLevel) : 0);
        return character.Level >= required || template.Kind is (ItemKind.Equipment or ItemKind.Avatar) &&
            character.Inventory.AccountWallet.ContractExpiry(22) > utcSeconds &&
            (long)character.Level + (contracts?.EquipmentLevelAllowance(template) ?? 0) >= required;
    }

    internal static bool RequiredSkillSatisfied(Character character, ItemTemplate template)
    {
        if (template.RequiredSkill == 0 || HasSkill(template.RequiredSkill))
            return true;
        ushort alternate = AlternateRequiredSkill(character, template);
        return alternate != 0 && HasSkill(alternate);

        bool HasSkill(ushort id) => character.Skills.Any(skill => skill.Id == id && skill.Level != 0);
    }

    // 匹配 CHR 的小魔女扫把准入为技能368；穿戴与助手保护共用，不补赠魔道的91。
    internal static ushort AlternateRequiredSkill(Character character, ItemTemplate template) =>
        template is { Kind: ItemKind.Equipment, EquipmentSlot: 12, WeaponSubType: 4, RequiredSkill: 91 } &&
        character.Job == 3 && (character.GrowType & 0x0f) == 5 ? (ushort)368 : (ushort)0;

    static InventoryItem PrepareForDestination(InventoryItem item, InventorySlot destination, InventoryItem? previous, ItemCatalog catalog)
    {
        var source = item.Position;
        var core = item.Core;
        if (source.List != InventoryListType.Equipment && destination.List != InventoryListType.Equipment &&
            (source.List != destination.List || source.List == InventoryListType.Main &&
            (source.Index is >= 3 and <= 8 || destination.Index is >= 3 and <= 8)))
            core = core with { SortLockFlag = 0 };
        if (destination.List == InventoryListType.Equipment && core.Kind is (ItemKind.Equipment or ItemKind.CreatureEquipment or ItemKind.GuildMedal or ItemKind.Talisman) &&
            core.SealFlag != 0 && catalog.Get(core.ItemId).AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase))
            core = core with { SealFlag = 0 };
        var avatar = item.Avatar;
        // 离开穿戴槽即解除克隆；替换时先从原穿戴快照取来源，再清理被退回的实例。
        if (avatar is not null)
        {
            var template = catalog.Get(core.ItemId);
            if (template.CloneAvatar && template.BorrowAvatarAppearance &&
                source.List == InventoryListType.Equipment && source != destination)
                avatar = InventoryCloneAppearanceService.Clear(avatar);
            else if (template.CloneAvatar && template.BorrowAvatarAppearance &&
                destination.List == InventoryListType.Equipment && previous?.Avatar is { } prior &&
                (avatar.ClearAvatarId == 0 || !catalog.Get(previous.Core.ItemId).CloneAvatar))
            {
                bool previousClone = catalog.Get(previous.Core.ItemId).CloneAvatar;
                avatar = avatar with
                {
                    ClearAvatarId = previousClone ? prior.ClearAvatarId : previous.Core.ItemId,
                    ClearAvatarSourceUid = previousClone ? prior.ClearAvatarSourceUid : previous.Uid,
                    Color1 = prior.Color1,
                    Color2 = prior.Color2
                };
            }
            else if (template.CloneAvatar && avatar.ClearAvatarId == 0)
                avatar = avatar with { ClearAvatarSourceUid = 0, Color1 = 0, Color2 = 0 };
        }
        return item with { Position = destination, Core = core, Avatar = avatar };
    }
}
