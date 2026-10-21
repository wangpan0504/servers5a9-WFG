using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryMoveService
{
    // ACK19 的 23 显示“无法移动该物品”；1 是地区限制，不能用于普通物品校验失败。
    const ushort CannotMoveItem = 23;

    public static InventoryResult Plan(Character character, InventoryMoveRequest request, ItemCatalog catalog, long utcSeconds,
        PremiumContractCatalog? contracts = null)
    {
        if (request.Source == new InventorySlot(InventoryListType.Equipment, 30) ||
            request.Destination == new InventorySlot(InventoryListType.Equipment, 30))
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
        // 当前客户端把个人金库格号写进物品编号：空格存入、从该格取出，以及丢到已占用格（编号为 0 或格号）。
        // 0 仍是空格哨兵，不能把 0 号格的真实物品当成别名。已占用格只在编号对不上模板时改投空格，避免盖掉原物品。
        bool sourceMatches = source is not null && PersonalCargoCell(request.Source, request.SourceItemId) ||
            (source?.Core.ItemId ?? 0) == request.SourceItemId;
        bool destinationMatches = destination is null && PersonalCargoCell(request.Destination, request.DestinationItemId) ||
            (destination?.Core.ItemId ?? 0) == request.DestinationItemId ||
            destination is not null && request.Destination.List == InventoryListType.PersonalCargo &&
            (request.DestinationItemId == 0 || PersonalCargoCell(request.Destination, request.DestinationItemId));
        bool throwOntoOccupied = destination is not null && destinationMatches &&
            request.Destination.List == InventoryListType.PersonalCargo &&
            destination.Core.ItemId != request.DestinationItemId &&
            (request.DestinationItemId == 0 || PersonalCargoCell(request.Destination, request.DestinationItemId));
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
        if (throwOntoOccupied && sourceSlot.List != InventoryListType.PersonalCargo &&
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
            if (!Allows(character, position, core, catalog, utcSeconds, contracts))
                return false;
            // 只有本次实际穿戴依赖霸王等级加成时冻结期限；卸装、普通移格和原等级合格不受其到期限制。
            if (position.List == InventoryListType.Equipment && position.Index != InventoryCreatureSkinService.AuraTargetSlot &&
                core.Kind is (ItemKind.Equipment or ItemKind.Avatar) &&
                character.Level < catalog.Get(core.ItemId).MinimumLevel)
                qualificationExpiresAt = character.Inventory.AccountWallet.ContractExpiry(22);
            return true;
        }

        void DecreaseSource()
        {
            if (count == source.Core.Count)
                planned.Remove(sourceSlot);
            else
                planned[sourceSlot] = source with { Core = source.Core with { Value = source.Core.Value - count } };
        }
    }

    static bool PersonalCargoCell(InventorySlot slot, int requestedId) =>
        slot.List == InventoryListType.PersonalCargo && slot.Index > 0 && requestedId == slot.Index;

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
        PremiumContractCatalog? contracts = null)
    {
        if (!ItemSlotBoundService.Allows(character.Inventory, position, core.Kind))
            return false;
        if (position.List != InventoryListType.Equipment)
            return true;
        var template = catalog.Get(core.ItemId);
        if (IsCreatureSkin(position))
            return (character.EquipmentSlotFlags & InventoryCreatureSkinService.UnlockFlag) != 0 && CreatureSkinTemplate(core, template);
        if (position.Index == InventoryCreatureSkinService.AuraTargetSlot)
            return (character.EquipmentSlotFlags & InventoryCreatureSkinService.AuraUnlockFlag) != 0 &&
                core.Kind == ItemKind.Avatar && template.Id == core.ItemId && template.EquipmentSlot is 9 or 11;
        int unlock = position.Index switch { 22 => 1, 23 => 2, 25 => 16, _ => 0 };
        if (unlock != 0 && (character.EquipmentSlotFlags & unlock) == 0) return false;
        // 女鬼剑流浪武士的副武器栏接受普通武器；觉醒位不改变基础转职。
        bool matchesSlot = template.EquipmentSlot == position.Index ||
            core.Kind == ItemKind.Avatar && template.EquipmentSlot == 9 && position.Index == 11 ||
            template.EquipmentSlot == 12 && position.Index == 24 && character.Job == 11 && (character.GrowType & 0x0f) == 4;
        // 宠物容器先核对物品部位；角色等级、职业和技能不能代替宠物成长系统的规则。
        if (core.Kind is ItemKind.Creature or ItemKind.CreatureEquipment)
            return matchesSlot;
        return matchesSlot && WearLevelSatisfied(character, template, utcSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), contracts) &&
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
        PremiumContractCatalog? contracts = null) =>
        character.Level >= template.MinimumLevel || template.Kind is (ItemKind.Equipment or ItemKind.Avatar) &&
        character.Inventory.AccountWallet.ContractExpiry(22) > utcSeconds &&
        (long)character.Level + (contracts?.EquipmentLevelAllowance(template) ?? 0) >= template.MinimumLevel;

    internal static bool RequiredSkillSatisfied(Character character, ItemTemplate template)
    {
        if (template.RequiredSkill == 0 || HasSkill(template.RequiredSkill))
            return true;
        // 匹配 CHR 的小魔女扫把准入为技能368；不补赠魔道的91，也不推广到其他武器要求。
        return template is { Kind: ItemKind.Equipment, EquipmentSlot: 12, WeaponSubType: 4, RequiredSkill: 91 } &&
            character.Job == 3 && (character.GrowType & 0x0f) == 5 && HasSkill(368);

        bool HasSkill(ushort id) => character.Skills.Any(skill => skill.Id == id && skill.Level != 0);
    }

    static InventoryItem PrepareForDestination(InventoryItem item, InventorySlot destination, InventoryItem? previous, ItemCatalog catalog)
    {
        var source = item.Position;
        var core = item.Core;
        if (source.List != InventoryListType.Equipment && destination.List != InventoryListType.Equipment &&
            (source.List != destination.List || source.List == InventoryListType.Main &&
            (source.Index is >= 3 and <= 8 || destination.Index is >= 3 and <= 8)))
            core = core with { SortLockFlag = 0 };
        if (destination.List == InventoryListType.Equipment && core.Kind is (ItemKind.Equipment or ItemKind.CreatureEquipment or ItemKind.GuildMedal) &&
            core.SealFlag != 0 && catalog.Get(core.ItemId).AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase))
            core = core with { SealFlag = 0 };
        var avatar = item.Avatar;
        if (avatar is not null)
        {
            var template = catalog.Get(core.ItemId);
            bool borrow = template.CloneAvatar && template.BorrowAvatarAppearance && destination.List == InventoryListType.Equipment;
            int appearance = 0;
            long sourceUid = 0;
            AvatarDetail? previousAppearance = null;
            // 空槽无原件，所有部位都保持零来源，由客户端沿原有外观路径显示。
            if (borrow && previous?.Avatar is { } prior)
            {
                bool previousClone = catalog.Get(previous.Core.ItemId).CloneAvatar;
                appearance = previousClone ? prior.ClearAvatarId : previous.Core.ItemId;
                sourceUid = previousClone ? prior.ClearAvatarSourceUid : previous.Uid;
                previousAppearance = prior;
            }
            avatar = avatar with
            {
                ClearAvatarId = appearance,
                ClearAvatarSourceUid = sourceUid,
                Color1 = template.CloneAvatar ? previousAppearance?.Color1 ?? 0 : avatar.Color1,
                Color2 = template.CloneAvatar ? previousAppearance?.Color2 ?? 0 : avatar.Color2
            };
        }
        return item with { Position = destination, Core = core, Avatar = avatar };
    }
}
