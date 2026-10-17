using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal sealed record ExperienceConsumablePlan(Character Expected, InventoryItem Source,
    ExperienceConsumableDefinition Definition, InventoryPlan Inventory, CharacterGrowthPlan Growth);

internal sealed class ExperienceConsumableService(ItemCatalog items, CharacterGrowthCatalog growth, EpicPieceCatalog pieces,
    TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public ExperienceConsumablePlan Prepare(Character expected, InventorySlot slot)
    {
        var now = Clock.GetUtcNow();
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.Kind != ItemKind.Consumable || source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable) || expected.DungeonState is null)
            throw new InvalidDataException("经验物品源实例无效");
        var definition = items.ExperienceConsumable(source.Core.ItemId);
        if (definition is null || !definition.Allows(expected.Level, expected.Job))
            throw new InvalidDataException("经验物品资源、职业或等级资格不符");
        ulong amount = Calculate(definition.Percent, definition.Amount, growth.GetLevelExperienceSpan(expected.Level));
        var result = CharacterGrowthPlan.AddExperience(expected, amount, growth);
        if (!result.Success || result.Plan!.GainedExperience is 0 or > uint.MaxValue)
            throw new InvalidDataException("经验物品没有可到账经验或成长规划无效");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("经验物品不能消费一件");
        var materials = InventoryMaterialConsumptionService.Plan(deletion.Plan!.After, definition.Materials, pieces, now);
        if (!materials.Accepted) throw new InvalidDataException("经验物品附加材料不足");
        return new(expected, source, definition, new(expected.Inventory, materials.Plan!.After, "use_experience_consumable", new(expected)), result.Plan);
    }

    internal static ulong Calculate(bool percent, int amount, ulong levelSpan)
    {
        if (amount <= 0) throw new InvalidDataException("经验物品增量必须为正");
        return percent ? checked((ulong)((UInt128)levelSpan * (uint)amount / 100)) : (uint)amount;
    }
}
