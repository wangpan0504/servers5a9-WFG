namespace DfoServer.GameWorld.Items;

// 重开活动的限购按角色独立计算，保留原刷新周期和不限购商品。
internal static class EpicRoad2019ShopLimits
{
    internal static ItemShopPrice Apply(int id, ItemShopPrice price) => id is
        490013751 or 490013718 or 490013719 or 490013724 or 490013810 or 490013725 or 490013726 or 490013729
        ? price with
        {
            Limit = price.Limit is { } limit ? limit with { AccountWide = false, Count = 5 } : null,
            PeriodicLimit = price.PeriodicLimit is { } periodic ? periodic with { AccountWide = false, Count = 5 } : null
        } : price;
}
