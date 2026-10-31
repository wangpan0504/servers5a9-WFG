using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class ShopPurchaseLimitService
{
    public static bool TryPlan(InventorySnapshot before, ItemShopPrice price, int npcId, int itemId, int count,
        DateTimeOffset now, out InventorySnapshot after, out long? deadline)
    {
        after = before;
        deadline = null;
        int today = ShopPurchaseCount.Today(now);
        foreach (var limit in new[] { price.Limit, price.PeriodicLimit })
        {
            if (limit is null) continue;
            bool periodic = limit.Period != ItemPurchasePeriod.Legacy;
            // 新声明按商品共享额度，不能通过换商人/商店页绕过；旧式限购仍按原商人保存。
            var key = new ShopPurchaseCount(periodic ? 0 : npcId, itemId, limit.AccountWide, limit.ResetDaily,
                periodic ? ShopPurchaseCount.PeriodDay(today, limit.Period) : limit.ResetDaily ? today : 0, count)
            { Period = limit.Period };
            var previous = after.ShopPurchases.SingleOrDefault(key.SameKey);
            int used = previous?.Effective(today) ?? 0;
            if (count <= 0 || previous?.Day > key.Day || (long)used + count > limit.Count)
            {
                after = before;
                deadline = null;
                return false;
            }
            key = key with { Count = used + count };
            after = after with { ShopPurchases = previous is null ? after.ShopPurchases.Add(key) : after.ShopPurchases.Replace(previous, key) };
            if (key.NextReset(today) is { } reset) deadline = Math.Min(deadline ?? long.MaxValue, reset);
        }
        return true;
    }
}
