using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.TagEvildom;

internal static class TagEvildomInventory
{
    // 与1394的Appearance/Player/Runes实际来源一致；名称装饰有独立到期过滤，不在这里拒绝。
    public static bool Projected(InventoryItem item) =>
        item.Position.List == InventoryListType.Equipment && item.Position.Index != 30 ||
        item.Position.List == InventoryListType.Talisman && item.Position.Index is >= 210 and <= 215;

    public static bool IsCurrent(InventorySnapshot inventory, ItemCatalog? catalog, long now)
    {
        var projected = inventory with { Items = inventory.Items.Where(Projected).ToImmutableArray() };
        if (projected.Items.Any(item => item.IsExpired(now))) return false;
        // 只读复用普通选角修复判定，不在TAG构包时删除资产或补写零期限；需要变化就先真正登录备用。
        return InventoryExpiryService.RepairZeroPeriod(projected, catalog).Plan is { Changed: false };
    }
}
