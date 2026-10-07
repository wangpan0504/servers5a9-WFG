using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryMoveService
{
    // ACK19 的 23 显示“无法移动该物品”；1 是地区限制，不能用于普通物品校验失败。
    const ushort CannotMoveItem = 23;

    public static InventoryResult Plan(Character character, InventoryMoveRequest request, ItemCatalog catalog, long utcSeconds)
    {
        // 调用方在owner内取得稳定输入；复制技能数组，后续资格读取和前提使用同一份快照。
        character = character with { Skills = [.. character.Skills] };
        var precondition = new InventoryCharacterPrecondition(character);
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var destination = before.Find(request.Destination);
        if (request.Actor is not (-1 or 0) || request.Flag0 != 0 || request.Flag1 != 0 || request.DestinationCount != 0 ||
            (source?.Core.ItemId ?? 0) != request.SourceItemId || (destination?.Core.ItemId ?? 0) != request.DestinationItemId)
            return InventoryResult.Reject(CannotMoveItem);
        // 029DA4CE/029DA611 写入模板编号，空格写0；先按原顺序校验，避免重复穿戴请求被反向解释为卸装。
        var sourceSlot = request.Source;
        var destinationSlot = request.Destination;
        if (source is null)
        {
            (source, destination) = (destination, source);
            (sourceSlot, destinationSlot) = (destinationSlot, sourceSlot);
        }
        if (source is null || source.IsExpired(utcSeconds) || destination?.IsExpired(utcSeconds) == true)
            return InventoryResult.Reject(CannotMoveItem);
        int count = InventoryStackRuleService.NormalizeMoveCount(source.Core, request.Count);
        if (count <= 0 || !Allows(character, destinationSlot, source.Core, catalog) ||
            sourceSlot.List != destinationSlot.List && !InventoryCargoService.AllowsInsert(source.Core, destinationSlot.List, catalog.Get(source.Core.ItemId)))
            return InventoryResult.Reject(CannotMoveItem);
        if (sourceSlot == destinationSlot)
            return new(true, 0, new(before, before, "move_no_change", precondition));
        if (source.Core.IsStackable && sourceSlot.List != destinationSlot.List)
        {
            // 跨容器存取优先完整合并，不能合并时仍允许空格；同容器手动拆分不走自动选格。
            int limit = catalog.Get(source.Core.ItemId).StackLimit;
            var mergeTarget = before.Items.Where(item => item.Position.List == destinationSlot.List &&
                InventoryStackRuleService.CanMerge(source.Core, item.Core, count, limit) &&
                Allows(character, item.Position, source.Core, catalog)).MinBy(item => item.Position.Index);
            if (mergeTarget is not null)
            {
                destinationSlot = mergeTarget.Position;
                destination = mergeTarget;
            }
            else if (destination is not null)
            {
                var empty = FindEmptyDestination(character, source.Core, destinationSlot.List, catalog);
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
            if (count != source.Core.Count || !Allows(character, sourceSlot, destination.Core, catalog) ||
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
        return new(true, 0, new(before, after, "move_item", precondition), count,
            forward ? sourceSlot : destinationSlot, forward ? destinationSlot : sourceSlot);

        void DecreaseSource()
        {
            if (count == source.Core.Count)
                planned.Remove(sourceSlot);
            else
                planned[sourceSlot] = source with { Core = source.Core with { Value = source.Core.Value - count } };
        }
    }

    static InventorySlot? FindEmptyDestination(Character character, ItemCore core, InventoryListType list, ItemCatalog catalog)
    {
        var inventory = character.Inventory;
        var range = ItemSlotBoundService.Range(core.Kind, inventory.Expansion);
        if (list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo)
            range = (0, (short)((list == InventoryListType.PersonalCargo ? inventory.CargoCapacity : inventory.AccountCargo.Capacity) - 1));
        else if (list == InventoryListType.Main)
            range = (3, ItemSlotBoundService.List(core.Kind) == list ? range.End : (short)8);
        for (short slot = range.Start; slot <= range.End; slot++)
        {
            var position = new InventorySlot(list, slot);
            if (inventory.Find(position) is null && Allows(character, position, core, catalog))
                return position;
        }
        return null;
    }

    public static bool Allows(Character character, InventorySlot position, ItemCore core, ItemCatalog catalog)
    {
        if (!ItemSlotBoundService.Allows(character.Inventory, position, core.Kind))
            return false;
        if (position.List != InventoryListType.Equipment)
            return true;
        var template = catalog.Get(core.ItemId);
        // 女鬼剑流浪武士的副武器栏接受普通武器；觉醒位不改变基础转职。
        bool matchesSlot = template.EquipmentSlot == position.Index ||
            core.Kind == ItemKind.Avatar && template.EquipmentSlot == 9 && position.Index == 11 ||
            template.EquipmentSlot == 12 && position.Index == 24 && character.Job == 11 && (character.GrowType & 0x0f) == 4;
        // 宠物容器先核对物品部位；角色等级、职业和技能不能代替宠物成长系统的规则。
        if (core.Kind is ItemKind.Creature or ItemKind.CreatureEquipment)
            return matchesSlot;
        return matchesSlot && character.Level >= template.MinimumLevel &&
            (template.Jobs & (1 << character.Job)) != 0 &&
            (template.RequiredSkill == 0 || character.Skills.Any(skill => skill.Id == template.RequiredSkill && skill.Level != 0));
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
            bool borrow = template.CloneAvatar && template.BorrowAvatarAppearance && destination.List == InventoryListType.Equipment &&
                previous?.Avatar is not null && !catalog.Get(previous.Core.ItemId).CloneAvatar;
            avatar = avatar with
            {
                ClearAvatarId = borrow ? previous!.Core.ItemId : 0,
                Color1 = template.CloneAvatar ? borrow ? previous!.Avatar!.Color1 : (ushort)0 : avatar.Color1,
                Color2 = template.CloneAvatar ? borrow ? previous!.Avatar!.Color2 : (ushort)0 : avatar.Color2
            };
        }
        return item with { Position = destination, Core = core, Avatar = avatar };
    }
}
