using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryExpiryService
{
    public static InventoryResult Plan(InventorySnapshot before, long utcSeconds)
    {
        // 锁不延长期限；按实例清理也同时回收该实例携带的锁 ID。
        var after = before with { Items = before.Items.Where(item => !item.IsExpired(utcSeconds)).ToImmutableArray() };
        return new(true, 0, new(before, after, "item_expired"));
    }

    // 带 [usable period]/[expiration date] 的模板，实例 ExpireTime=0 会被客户端 029B7090 当成剩余 0 秒（「已超过使用期限」）并本地拦截移动。
    // 与 GM ClearedExpire 同一口径：写成协议上限 2147483647；时装/宠物核心 0 表示永久，不能改。
    public static InventoryResult RepairZeroPeriod(InventorySnapshot before, ItemCatalog? catalog)
    {
        if (catalog is null)
            return new(true, 0, new(before, before, "item_expire_repaired"));
        var after = before with { Items = before.Items.Select(item => RepairZeroPeriodItem(item, catalog)).ToImmutableArray() };
        return new(true, 0, new(before, after, "item_expire_repaired"));
    }

    static InventoryItem RepairZeroPeriodItem(InventoryItem item, ItemCatalog catalog)
    {
        if (item.Core.ExpireTime != 0 || item.Core.Kind is ItemKind.Avatar or ItemKind.Creature)
            return item;
        ItemTemplate template;
        try { template = catalog.Get(item.Core.ItemId); }
        catch (InvalidDataException) { return item; }
        if (template.Expiration.Days <= 0 && template.Expiration.AbsoluteTime <= 0)
            return item;
        return item with { Core = item.Core with { ExpireTime = int.MaxValue } };
    }
}
