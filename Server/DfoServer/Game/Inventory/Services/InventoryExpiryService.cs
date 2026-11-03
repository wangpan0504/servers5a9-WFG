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
    // 这类模板写成协议上限 2147483647；时装/宠物核心 0 表示永久，不能改。
    // 存量 A 不追溯：daily/weekly 且 ExpireTime=0 的历史实例保持 0，不补下一 06:00、不删除。
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
        // 周删除旧实例没有已冻结截止，不能借历史修复写成永久或重算下一周。
        // 纯 daily（无 usable/absolute）同样保留历史 0：6 点钩子与选角都不追溯。
        if (template.Expiration.WeeklyDeleteDay is not null ||
            template.Expiration.Days <= 0 && template.Expiration.AbsoluteTime <= 0)
            return item;
        return item with { Core = item.Core with { ExpireTime = int.MaxValue } };
    }
}
