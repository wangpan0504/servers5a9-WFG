using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.CashShopPromotions;

// 只解释当前商品归属并冻结完整赠礼；在线状态与事务仍由Session拥有。
internal sealed class CashShopPromotionService(Func<CashShopPromotionCatalog> current, Func<int, ItemTemplate> item)
{
    public CashShopPromotionCatalog Current => current();

    public static PromotionCatalogEntry? Select(CashShopPromotionCatalog catalog, CashShopPurchaseRequest request)
    {
        PromotionCatalogEntry? selected = null;
        foreach (var row in request.Items)
        {
            var entry = catalog.Product(row.CommodityId);
            if (entry?.Availability == PromotionAvailability.Invalid)
                throw new InvalidDataException("促销商品尚未满足启用条件");
            if (entry?.Availability != PromotionAvailability.Active) continue;
            var rules = entry.Configuration.Rules;
            if (selected is not null && selected.Configuration.Rules.Scope != rules.Scope)
                throw new InvalidDataException("单车暂不支持多个已启用促销组");
            selected = entry;
        }
        return selected;
    }

    public PromotionPurchasePlan Plan(Character character, InventoryPlan inventory, CashShopPurchaseReceipt purchase,
        PromotionProgress progress, PromotionRules rules, long revision, string account, DateTimeOffset now) =>
        Plan(character, inventory, purchase, progress, rules, revision, account, now, PromotionPolicy.Pilot(rules));

    public PromotionPurchasePlan Plan(Character character, InventoryPlan inventory, CashShopPurchaseReceipt purchase,
        PromotionProgress progress, PromotionCatalogEntry entry, long revision, string account, DateTimeOffset now) =>
        Plan(character, inventory, purchase, progress, entry.Configuration.Rules, revision, account, now,
            entry.Policy ?? throw new InvalidDataException("促销策略无效"));

    PromotionPurchasePlan Plan(Character character, InventoryPlan inventory, CashShopPurchaseReceipt purchase,
        PromotionProgress progress, PromotionRules rules, long revision, string account, DateTimeOffset now, PromotionPolicy policy)
    {
        if (progress.Scope != policy.CounterScope) throw new InvalidDataException("促销累计身份与策略不一致");
        int units = 0;
        foreach (var product in purchase.Products)
        {
            var mapping = rules.Products.SingleOrDefault(row => row.CommodityId == product.CommodityId);
            if (mapping is null) continue;
            if (mapping.ItemId != product.ItemId || product.Count != 1)
                throw new InvalidDataException("实际购买商品与促销单位不一致");
            units = checked(units + mapping.Units);
        }
        var awards = CashShopPromotionRules.Settle(rules, progress.Count, units, policy);
        var order = new PromotionOrder(Guid.NewGuid(), account, character.Id, rules.Scope,
            progress.Count, progress.Revision, units, now);
        var recipient = new MailboxRecipient(character.Id, account, character.Name, character.Job, character.GrowType, character.Level);
        var deliveries = ImmutableArray.CreateBuilder<MailboxDelivery>();
        for (int offset = 0; offset < awards.Length; offset += 10)
        {
            var attachments = ImmutableArray.CreateBuilder<InventoryItem>();
            foreach (var award in awards.Skip(offset).Take(10))
            {
                var template = item(award.ItemId);
                attachments.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 65),
                    InventoryRewardGrantService.Create(template, award.Count, now)));
            }
            deliveries.Add(new(PromotionMailKey.Create(order, awards[offset].PurchaseNumber, offset / 10), recipient,
                "商城赠礼", "累计购买赠礼，请查收附件。", 0, attachments.ToImmutable(), now.ToUnixTimeSeconds()));
        }
        return new(order, character, inventory, purchase, awards, deliveries.ToImmutable()) { Rules = rules, Policy = policy, CatalogRevision = revision };
    }
}
