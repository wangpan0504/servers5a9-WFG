using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

// 本服兼容：基础增长5按百分比解释，二十次真实远古通关触发一次资源表内额外箱。
// 复用角色日账本的箱组键保存进度；仅在原通关事务写入，次日06:00自然归零。
internal sealed record AncientGateClear(ImmutableArray<DailyBoosterUsage> Before, ImmutableArray<DailyBoosterUsage> After,
    bool Bonus, uint Energy);

internal static class AncientGateProgression
{
    public const int CounterItem = 10159543;
    public const int PercentPerClear = 5;
    public const uint Maximum = 10000;

    public static uint Energy(InventorySnapshot inventory, DateTimeOffset now) =>
        checked((uint)(DailyBoosterUsage.Effective(inventory.DailyBoosterUses, CounterItem, ShopPurchaseCount.Today(now)) * PercentPerClear * 100));

    public static AncientGateClear Prepare(InventorySnapshot inventory, DateTimeOffset now)
    {
        int day = ShopPurchaseCount.Today(now);
        int steps = DailyBoosterUsage.Effective(inventory.DailyBoosterUses, CounterItem, day) + 1;
        bool bonus = steps * PercentPerClear >= 100;
        var after = inventory.DailyBoosterUses.Where(row => row.ItemId != CounterItem).ToImmutableArray();
        if (!bonus) after = after.Add(new(CounterItem, day, checked((byte)steps)));
        return new(inventory.DailyBoosterUses, after.OrderBy(row => row.ItemId).ToImmutableArray(), bonus,
            bonus ? 0 : checked((uint)(steps * PercentPerClear * 100)));
    }
}
