using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static readonly JsonSerializerOptions PromotionPageJson = new()
    {
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    // 历史投影不读取角色当前资产及邮箱状态。
    sealed record PageExpected(ushort Id, string Name);
    sealed record PageBusiness(PromotionOrder Order, PageExpected Expected, string Rules,
        CashShopProduct[] Products, PromotionAward[] Awards, MailboxDelivery[] Deliveries);
    sealed record PageReward(PromotionAward Award, InventoryItem Item);
    sealed record PageReceipt(string OrderId, PromotionAward Award, uint MailId, InventoryItem PlannedItem);

    public PromotionLedgerSnapshot ReadPage(AccountOwners.Lease owner, PromotionCatalogEntry entry) => owner.Execute(() =>
    {
        var rules = entry.Configuration.Rules;
        var scope = rules.Scope;
        using var db = database.Open();
        using var tx = db.BeginRead();
        using var command = PromotionCommand(db, tx, scope, owner.Account);
        command.CommandText = $"SELECT count,revision FROM cash_shop_promotion_progress WHERE account=$account AND {PromotionScopeFilter}";
        bool hasProgress;
        PromotionProgress progress;
        using (var reader = command.ExecuteReader())
        {
            hasProgress = reader.Read();
            progress = hasProgress ? new(scope, reader.GetInt64(0), reader.GetInt64(1)) : new(scope, 0, 0);
        }
        PromotionLedgerSnapshot Unverified() => new(progress, null, null, false);
        try
        {
            // 页面格式固定描述三轮、每轮九档奖励。
            var policy = entry.Policy;
            if (policy is null || policy.IsPermanent || policy.CounterScope != scope ||
                rules.CycleLength != 10 || rules.Cycles != 3 || rules.OverflowFrom != 31 ||
                rules.MaximumPurchases != 5000 || rules.Tiers.Length != 9 ||
                !rules.Tiers.Select(tier => tier.Sequence).Order().SequenceEqual(Enumerable.Range(2, 9)) ||
                progress.Count < 0 || progress.Revision < 0)
                return Unverified();
            string snapshot = CashShopPromotionCatalog.RuleSnapshot(rules, policy);
            command.CommandText = $"SELECT rules_version,rules_snapshot,rule_kind,carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group,carry_rules_snapshot FROM cash_shop_promotion_rules WHERE {PromotionScopeFilter}";
            bool hasRules;
            using (var reader = command.ExecuteReader())
            {
                hasRules = reader.Read();
                if (hasRules && (reader.GetString(0) != scope.RulesVersion || reader.GetString(1) != snapshot ||
                    reader.GetInt64(2) != 0 || Enumerable.Range(3, 6).Any(index => !reader.IsDBNull(index))))
                    return Unverified();
            }
            command.CommandText = "SELECT 1 FROM cash_shop_promotion_rules WHERE rule_kind=1 AND carry_campaign=$campaign " +
                "AND carry_period=$period AND carry_realm=$realm AND carry_group=$group AND carry_rules_version=$version LIMIT 1";
            command.Parameters.AddWithValue("$version", scope.RulesVersion);
            if (command.ExecuteScalar() is not null) return Unverified();

            var receipts = new Dictionary<long, PageReceipt>();
            var mailIds = new HashSet<uint>();
            long count = 0, revision = 0;
            command.CommandText = $"SELECT order_id,recipient_id,rules_version,business_snapshot,count,revision,mail_ids,committed_at FROM cash_shop_promotion_orders WHERE account=$account AND {PromotionScopeFilter} ORDER BY revision,order_id";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    string orderId = reader.GetString(0);
                    var business = JsonSerializer.Deserialize<PageBusiness>(reader.GetString(3), PromotionPageJson);
                    var mails = JsonSerializer.Deserialize<uint[]>(reader.GetString(6), PromotionPageJson);
                    if (!hasRules || !hasProgress || business is null || mails is null ||
                        !Guid.TryParseExact(orderId, "N", out var id) || id == Guid.Empty || orderId != id.ToString("N") ||
                        reader.GetString(2) != scope.RulesVersion ||
                        !PageOrderMatches(business, id, owner.Account, rules, reader.GetInt64(1), count, revision, snapshot))
                        return Unverified();
                    var order = business.Order;
                    long after = checked(count + order.Units), nextRevision = checked(revision + 1);
                    if (reader.GetInt64(4) != after || reader.GetInt64(5) != nextRevision ||
                        reader.GetInt64(7) < order.PlannedAt.ToUnixTimeSeconds())
                        return Unverified();
                    var awards = PageOrderAwards(rules, count, order.Units);
                    if (!awards.SequenceEqual(business.Awards))
                    {
                        var legacyAwards = PageLegacyOrderAwards(rules, count, order.Units);
                        if (legacyAwards.SequenceEqual(business.Awards)) awards = legacyAwards;
                    }
                    int awardsPerMail = business.Deliveries.Length == awards.Count ? 1 : 10;
                    if (!awards.SequenceEqual(business.Awards) ||
                        business.Deliveries.Length != (awards.Count + awardsPerMail - 1) / awardsPerMail ||
                        mails.Length != business.Deliveries.Length)
                        return Unverified();
                    for (int part = 0; part < business.Deliveries.Length; part++)
                    {
                        var delivery = business.Deliveries[part];
                        int offset = part * awardsPerMail;
                        if (delivery is null || delivery.Recipient is null ||
                            mails[part] is 0 or > 1073741823 || !mailIds.Add(mails[part]) ||
                            delivery.Key != PromotionMailKey.Create(order, awards[offset].PurchaseNumber, part) ||
                            delivery.Recipient.Id != order.RecipientId || delivery.Recipient.Account != owner.Account ||
                            delivery.Recipient.Name != business.Expected.Name || delivery.Gold != 0 || delivery.Expires is not null ||
                            delivery.Created != order.PlannedAt.ToUnixTimeSeconds() || delivery.Items.IsDefault ||
                            delivery.Items.Length != Math.Min(awardsPerMail, awards.Count - offset))
                            return Unverified();
                        for (int index = 0; index < delivery.Items.Length; index++)
                        {
                            var item = delivery.Items[index];
                            var award = awards[offset + index];
                            if (item is null || item.Core is null || item.Uid != 0 || item.Core.ItemId != award.ItemId ||
                                item.Core.Count != award.Count || item.IsExpired(delivery.Created))
                                return Unverified();
                            item.Core.Validate();
                            item.ValidateDetails(allocating: true);
                            if (!receipts.TryAdd(award.PurchaseNumber, new(orderId, award, mails[part], item)))
                                return Unverified();
                        }
                    }
                    count = after;
                    revision = nextRevision;
                }
            }
            if (count != progress.Count || revision != progress.Revision || hasProgress && !hasRules)
                return Unverified();

            uint mask = 0;
            long overflow = 0;
            var itemUids = new HashSet<long>();
            command.CommandText = $"SELECT global_purchase,order_id,rules_version,reward_snapshot,mail_id FROM cash_shop_promotion_awards WHERE account=$account AND {PromotionScopeFilter}";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    long purchase = reader.GetInt64(0);
                    if (!receipts.Remove(purchase, out var expected) || reader.GetString(1) != expected.OrderId ||
                        reader.GetString(2) != scope.RulesVersion || reader.GetInt64(4) != expected.MailId)
                        return Unverified();
                    var reward = JsonSerializer.Deserialize<PageReward>(reader.GetString(3), PromotionPageJson);
                    if (reward is null || reward.Award != expected.Award || reward.Item is not { Core: not null } actual ||
                        actual.Uid <= 0 || !itemUids.Add(actual.Uid))
                        return Unverified();
                    actual.Core.Validate();
                    actual.ValidateDetails(allocating: false);
                    // 新邮件道具的分配仅改变UID和特殊道具的Core.Value。
                    var planned = expected.PlannedItem;
                    var normalized = actual with { Uid = 0 };
                    if (actual.Avatar is not null || actual.Creature is not null)
                        normalized = normalized with { Core = actual.Core with { Value = planned.Core.Value } };
                    if (JsonSerializer.Serialize(normalized) != JsonSerializer.Serialize(planned))
                        return Unverified();
                    if (purchase >= 31) overflow = checked(overflow + 1);
                    else
                    {
                        int bit = checked((int)((purchase - 1) / 10 * 9 + (purchase - 1) % 10 - 1));
                        if (bit is < 0 or >= 27 || (mask & (1u << bit)) != 0) return Unverified();
                        mask |= 1u << bit;
                    }
                }
            }
            if (receipts.Count != 0 || overflow != Math.Max(0, progress.Count - 30)) return Unverified();
            return new PromotionLedgerSnapshot(progress, mask, overflow, true);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or OverflowException)
        {
            // owner失效与SQL错误保持原异常边界。
            return Unverified();
        }
    });

    static bool PageOrderMatches(PageBusiness business, Guid id, string account, PromotionRules rules,
        long recipientId, long count, long revision, string snapshot)
    {
        var order = business.Order;
        if (order is null || business.Expected is null || business.Products is not { Length: >= 1 and <= 64 } ||
            business.Awards is null || business.Deliveries is null ||
            order.Id != id || order.Account != account || order.Scope != rules.Scope || order.RecipientId != recipientId ||
            order.RecipientId == 0 || business.Expected.Id != order.RecipientId || string.IsNullOrEmpty(business.Expected.Name) ||
            order.ExpectedCount != count || order.ExpectedRevision != revision || order.Units is < 1 or > 64 ||
            business.Rules != snapshot)
            return false;
        int units = 0;
        foreach (var product in business.Products)
        {
            if (product is null) return false;
            var mapping = rules.Products.SingleOrDefault(row => row.CommodityId == product.CommodityId);
            if (mapping is null) continue;
            if (product.ItemId != mapping.ItemId || product.Count != 1) return false;
            units = checked(units + mapping.Units);
        }
        return units == order.Units;
    }

    static List<PromotionAward> PageOrderAwards(PromotionRules rules, long before, int units)
    {
        // 每个已存订单最多循环64次，与累计i64数值大小无关。
        var awards = new List<PromotionAward>();
        for (int offset = 1; offset <= units; offset++)
        {
            long purchase = checked(before + offset);
            int sequence = (int)((purchase - 1) % rules.CycleLength + 1);
            bool matchedTier = false;
            foreach (var tier in rules.Tiers)
            {
                if (tier.Sequence == sequence)
                {
                    awards.Add(new(purchase, tier.ItemId, tier.Count));
                    matchedTier = true;
                }
            }
            if (!matchedTier && purchase >= rules.OverflowFrom)
                awards.Add(new(purchase, rules.OverflowItemId, rules.OverflowCount));
        }
        return awards;
    }

    static List<PromotionAward> PageLegacyOrderAwards(PromotionRules rules, long before, int units)
    {
        var awards = new List<PromotionAward>();
        for (int offset = 1; offset <= units; offset++)
        {
            long purchase = checked(before + offset);
            if (purchase >= rules.OverflowFrom)
                awards.Add(new(purchase, rules.OverflowItemId, rules.OverflowCount));
            else
            {
                int sequence = (int)((purchase - 1) % rules.CycleLength + 1);
                foreach (var tier in rules.Tiers)
                    if (tier.Sequence == sequence) awards.Add(new(purchase, tier.ItemId, tier.Count));
            }
        }
        return awards;
    }
}
