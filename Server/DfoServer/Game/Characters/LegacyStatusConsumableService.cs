using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal sealed record LegacyStatusConsumablePlan(Character Expected, InventoryItem Source, LegacyStatusConsumableDefinition Definition,
    InventoryPlan Inventory);

internal sealed class LegacyStatusConsumableService(ItemCatalog items, GoldLimitCatalog goldLimits, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public LegacyStatusConsumablePlan Prepare(Character expected, InventorySlot slot)
    {
        var now = Clock.GetUtcNow();
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.Kind != ItemKind.Consumable || source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable))
            throw new InvalidDataException("旧状态道具源实例无效");
        var definition = items.LegacyStatusConsumable(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsUse(expected.Level, expected.Job))
            throw new InvalidDataException("旧状态道具资源、职业或等级资格不符");
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("旧状态道具不能消费一件");
        var after = deletion.Plan!.After;
        var granted = new CurrencyService(goldLimits).Grant(expected with { Inventory = after }, CurrencyKind.ReviveCoin, definition.Amount);
        if (!granted.Accepted || granted.Plan!.After.ReviveCoins - after.ReviveCoins != definition.Amount)
            throw new InvalidDataException("复活币余额不能完整接收该券");
        return new(expected, source, definition, new(expected.Inventory, granted.Plan.After, "use_legacy_status",
            new(expected, source.ExpireTime > 0 ? source.ExpireTime : null, Clock)));
    }
}
