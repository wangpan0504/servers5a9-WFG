using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;

namespace DfoServer.Game.Inventory.Services;

// 商品、扣点券及资源明确声明的契约或实物奖励在同一个库存计划提交。
internal sealed class InventoryCashShopService(ItemCatalog catalog, CashShopCatalog shop, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    GameDebugSettings? debugSettings = null, PremiumContractCatalog? contracts = null)
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
            if (!AllowsReward(product.ItemId)) return InventoryResult.Reject(17);

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
                if (!AllowsReward(bonus.ItemId)) return InventoryResult.Reject(17);
                rewards.Add(new(bonus.ItemId, bonus.Count));
            }
        }

        var before = character.Inventory;
        var spent = CurrencyService.Spend(before, CurrencyKind.Cera, checked((int)total));
        if (!spent.Accepted)
            return InventoryResult.Reject(10);
        // 主商品和附赠按真实货币归属合计；付费购买不能沿普通发奖的上限截断规则少发奖励。
        if (!CanGrantCurrencies(spent.Plan!.After, rewards, new CurrencyService(goldLimits).CarryLimit(character)))
            return InventoryResult.Reject(17);
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = spent.Plan!.After }, rewards,
            catalog, goldLimits, pieces, utcNow, shopExpiration: shopExpiration, contracts: contracts, allowOverflowMail: true);
        if (!grant.Accepted)
            return grant;

        receipt = new(products.ToImmutableArray(), bonuses);
        return new(true, 0, new(before, grant.Plan!.After, "cash_shop_buy", overflowItems: grant.Plan.OverflowItems));

        bool AllowsReward(int itemId)
        {
            if (CurrencyWallet.RewardKind(itemId) is not null || pieces.ByPiece.ContainsKey(itemId)) return true;
            if (contracts?.Recognizes(itemId) == true) return contracts.TryAcquisitionContract(itemId, out _);
            try
            {
                var template = catalog.Get(itemId);
                return !template.IsPremiumService || template.AcquisitionContract is not null;
            }
            catch (InvalidDataException) { return false; }
        }
    }

    internal static bool CanGrantCurrencies(InventorySnapshot inventory, IReadOnlyList<InventoryReward> rewards, int goldLimit)
    {
        foreach (var group in rewards.GroupBy(reward => CurrencyWallet.RewardKind(reward.ItemId)))
        {
            if (group.Key is not { } kind)
                continue;
            long limit = kind == CurrencyKind.Gold ? goldLimit : int.MaxValue;
            if (CurrencyWallet.Get(inventory, kind) + group.Sum(reward => (long)reward.Count) > limit)
                return false;
        }
        return true;
    }
}
