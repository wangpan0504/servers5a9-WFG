using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

// CERA 商城的一次购买只负责“校验目录、扣点券、发放奖励”。契约、名称装扮和开盒使用保持在独立功能点，避免本次购买事务产生隐式副作用。
internal sealed class InventoryCashShopService(ItemCatalog catalog, CashShopCatalog shop, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    GameDebugSettings? debugSettings = null)
{
    static readonly string[] UnsupportedSections = ["visual", "selectable character premium", "charac premium package"];
    readonly GameDebugSettings debug = debugSettings ?? new();
    // 仅当前服务实例的调试开关；每次购买冻结一次状态，不改变其它发奖入口。
    public bool IgnoreAbsoluteExpiration
    {
        get => debug.IgnoreCashShopAbsoluteExpiration;
        set => debug.IgnoreCashShopAbsoluteExpiration = value;
    }

    public InventoryResult Buy(Character character, CashShopPurchaseRequest request, out CashShopPurchaseReceipt? receipt,
        DateTimeOffset? utcNow = null)
    {
        receipt = null;
        bool shopExpiration = IgnoreAbsoluteExpiration;
        if (request.PaymentMode != 0 || request.Items.Count is < 1 or > 64)
            return InventoryResult.Reject(17);

        var products = new List<CashShopProduct>(request.Items.Count);
        var rewards = new List<InventoryReward>(request.Items.Count);
        var bonuses = new Dictionary<int, IReadOnlyList<CashShopBonus>>();
        long total = 0;
        foreach (var requested in request.Items)
        {
            var product = shop.Product(requested.CommodityId);
            if (product is null || product.CeraPrice < 0 || product.Count <= 0 || UnsupportedSections.Contains(product.Section))
                return InventoryResult.Reject(17);
            try { catalog.Get(product.ItemId); }
            catch (InvalidDataException) { return InventoryResult.Reject(17); }

            total += product.CeraPrice;
            if (total > int.MaxValue)
                return InventoryResult.Reject(17);
            products.Add(product);
            rewards.Add(new(product.ItemId, product.Count));

            var productBonuses = shop.Bonus(product.CommodityId);
            if (productBonuses.Count == 0)
                continue;
            bonuses[product.CommodityId] = productBonuses;
            foreach (var bonus in productBonuses)
            {
                try { catalog.Get(bonus.ItemId); }
                catch (InvalidDataException) { return InventoryResult.Reject(17); }
                rewards.Add(new(bonus.ItemId, bonus.Count));
            }
        }

        var before = character.Inventory;
        var spent = CurrencyService.Spend(before, CurrencyKind.Cera, checked((int)total));
        if (!spent.Accepted)
            return InventoryResult.Reject(10);
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = spent.Plan!.After }, rewards,
            catalog, goldLimits, pieces, utcNow, shopExpiration: shopExpiration);
        if (!grant.Accepted)
            return grant;

        receipt = new(products.ToImmutableArray(), bonuses);
        return new(true, 0, new(before, grant.Plan!.After, "cash_shop_buy"));
    }
}
