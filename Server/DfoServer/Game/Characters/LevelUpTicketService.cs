using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal sealed record LevelUpTicketSource(InventoryItem Item, LevelUpTicketDefinition Definition, InventoryPlan Inventory,
    CharacterGrowthPlan Growth, long UtcSeconds)
{
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

internal sealed class LevelUpTicketService(ItemCatalog items, CharacterGrowthCatalog growth, EpicPieceCatalog pieces,
    TimeProvider? time = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public LevelUpTicketSource Prepare(Character expected, InventorySlot position)
    {
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var item = expected.Inventory.Find(position);
        if (position.List != InventoryListType.Main || item is null || item.Uid <= 0 || item.Core.Count <= 0 ||
            item.Core.Kind != ItemKind.Consumable || item.Core.EquipmentLockId != 0 || item.IsExpired(now) ||
            !ItemSlotBoundService.Allows(expected.Inventory, position, ItemKind.Consumable))
            throw new InvalidDataException("升级券实例或槽位无效");
        var definition = items.LevelUpTicket(item.Core.ItemId);
        if (definition is null || expected.DungeonState is null || !definition.Allows(expected.Level, expected.Job) ||
            expected.Level is < 1 or >= CharacterGrowthCatalog.MaximumSupportedLevel)
            throw new InvalidDataException("升级券等级、职业或资源资格不符");
        byte target = checked((byte)(expected.Level + 1));
        var result = CharacterGrowthPlan.Create(expected, target, growth.GetLevelStartExperience(target), growth);
        if (!result.Success || result.Plan!.GainedExperience > uint.MaxValue)
            throw new InvalidDataException("升级券成长规划失败: " + result.Failure);
        var deletion = InventoryDeleteService.Plan(expected.Inventory, position, item.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("升级券不能消费一张");
        var materials = InventoryMaterialConsumptionService.Plan(deletion.Plan!.After, definition.Materials, pieces, Clock.GetUtcNow());
        if (!materials.Accepted) throw new InvalidDataException("升级券附加材料不足");
        return new(item, definition, new(expected.Inventory, materials.Plan!.After, "level_up_ticket"), result.Plan, now) { Clock=Clock };
    }
}
