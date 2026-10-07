using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryExpiryService
{
    public static InventoryResult Plan(InventorySnapshot before, long utcSeconds)
    {
        // 锁不延长期限；按实例清理也同时回收该实例携带的锁 ID。
        var after = before with { Items = before.Items.Where(item => !item.IsExpired(utcSeconds)).ToImmutableArray() };
        return new(true, 0, new(before, after, "item_expired"));
    }
}
