using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

// 誓约之缚武器制作：消费本制作图，把装备ID换成本职业对应的哈林史诗。
// 制作图的[need material]是NPC商店卖价，不是使用成本，本服务不扣任何材料。
// 装备核心的其余字段（强化、增幅、锻造、附魔、品级、天数）随实例保留，不在平台层重建。
internal sealed class InventoryEquipmentPeriodChangeService(ItemCatalog catalog, TownCatalog? towns = null,
    TimeProvider? time = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public InventoryResult Plan(Character character, InventorySlot sourceSlot, InventorySlot targetSlot, bool inTown)
    {
        var now = Clock.GetUtcNow();
        var source = character.Inventory.Find(sourceSlot);
        var target = character.Inventory.Find(targetSlot);
        if (sourceSlot == targetSlot || source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds()) || source.Core.Count < 1 ||
            !ItemSlotBoundService.Allows(character.Inventory, sourceSlot, source.Core.Kind) ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Avatar: null, Creature: null } ||
            targetSlot.List is not (InventoryListType.Main or InventoryListType.Equipment) ||
            target.Core.EquipmentLockId != 0 || target.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(character.Inventory, targetSlot, target.Core.Kind))
            return InventoryResult.Reject(19);
        var definition = catalog.EquipmentPeriodChange(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsUse(character.Level, character.Job) ||
            !definition.AllowsSource(target.Core.ItemId))
            return InventoryResult.Reject(19);
        var replacementId = definition.Upgrades[target.Core.ItemId];
        var replacement = catalog.Get(replacementId);
        if (replacement.Kind != ItemKind.Equipment || !replacement.AllowsUse(character.Level, character.Job))
            return InventoryResult.Reject(19);
        string place = !inTown ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
            ? "[seria room]" : "[village]";
        if (!definition.AllowsPlace(place))
            return InventoryResult.Reject(19);
        // 制作图本身按源消费（含冷却可用性）；不追加任何材料扣费。
        var consumed = InventoryActionConsumableService.Plan(character, sourceSlot,
            new ActionConsumableDefinition(definition.Item, EquipmentPeriodChangeDefinition.Label, [], 0,
                definition.Item.Id, 0, 0, EquipmentPeriodChangeDefinition.Places.ToImmutableArray()), place, Clock, now);
        if (!consumed.Accepted)
            return consumed;
        var after = consumed.Plan!.After;
        // 只替换物品ID；强化、增幅、锻造、附魔与品级随实例原样保留。
        var upgraded = target with { Core = target.Core with { ItemId = replacementId } };
        if (after.Find(targetSlot) is not { } current)
            return InventoryResult.Reject(19);
        after = after with { Items = after.Items.Replace(current, upgraded) };
        // 保留源消费的冷却/日次数前提，同时防止规划期间目标到期后仍被旧计划升级。
        long? deadline = consumed.Plan.CharacterPrecondition!.QualificationExpiresAt;
        if (target.ExpireTime > 0)
            deadline = Math.Min(deadline ?? long.MaxValue, target.ExpireTime);
        return new(true, 0, new(character.Inventory, after, "equipment_period_changed",
            new(character, deadline, Clock, requirePosition: true)));
    }
}
