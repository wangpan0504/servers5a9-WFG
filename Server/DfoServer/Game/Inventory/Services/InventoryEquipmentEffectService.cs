using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEquipmentEffectService(ItemCatalog items, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, short sourceSlot, short targetSlot)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, sourceSlot));
        var target = character.Inventory.Find(new(InventoryListType.Main, targetSlot));
        if (source is null || target is null) return InventoryResult.Reject(19);
        return Plan(character, sourceSlot, targetSlot, items.EquipmentEffect(source.Core.ItemId),
            items.Get(target.Core.ItemId), Clock);
    }

    internal static InventoryResult Plan(Character character, short sourceSlot, short targetSlot,
        EquipmentEffectDefinition? definition, ItemTemplate targetTemplate, TimeProvider clock)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, sourceSlot));
        var target = before.Find(new(InventoryListType.Main, targetSlot));
        if (sourceSlot == targetSlot || source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Avatar: null, Creature: null } ||
            source.Uid == target.Uid || source.Core.Count <= 0 || source.Core.EquipmentLockId != 0 ||
            target.Core.EquipmentLockId != 0 || target.Core.IsRental || target.Core.SealFlag != 0 ||
            source.IsExpired(now) || target.IsExpired(now) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) ||
            definition is null || definition.Item.Id != source.Core.ItemId ||
            !definition.Item.AllowsUse(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, definition.Item) ||
            targetTemplate.Id != target.Core.ItemId || targetTemplate.Kind != ItemKind.Equipment ||
            targetTemplate.EquipmentSlot != 12 || targetTemplate.Rarity < 2 || target.Core.Rune == definition.Effect)
            return InventoryResult.Reject(19);
        var deletion = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!deletion.Accepted) return deletion;
        var after = deletion.Plan!.After;
        // 024B2D90读取123B条目+45h；只改既有特效编号，完整保留UID和打造。
        after = after with { Items = after.Items.Replace(target, target with { Core = target.Core with { Rune = definition.Effect } }) };
        long? deadline = new[] { source, target }.Where(item => item.ExpireTime > 0).Select(item => (long?)item.ExpireTime).Min();
        return new(true, 0, new(before, after, "equipment_effect", new(character, deadline, clock)));
    }
}
