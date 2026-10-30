using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.CashShopPromotions;

internal sealed class CashShopPromotionPageService(ICashShopPromotionStore store, Func<int, ItemTemplate> item)
{
    internal static readonly PromotionScope Scope = new("spring-2020", "replica-1", "v1", "default", "package");
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal PromotionPageSnapshot Read(AccountOwners.Lease owner, CashShopPromotionCatalog catalog, int ttlMs)
    {
        owner.Execute(() => true);
        if (ttlMs is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(ttlMs));
        PromotionPageSnapshot Unavailable(PromotionProgress? knownProgress) => new(Scope.Campaign, Scope.Period,
            Scope.RulesVersion, Scope.Realm, Scope.Group, catalog.Revision, knownProgress?.Revision, "unavailable",
            knownProgress?.Count, 5000, null, null, ttlMs, [], null);
        var entry = catalog.Activities.SingleOrDefault(candidate => candidate.Configuration.Rules.Scope == Scope);
        if (entry is null) return Unavailable(null);
        // 仓储失效与SQL异常沿原边界传播；先取得原累计，再做锁外名称查询。
        var ledger = store.ReadPage(owner, entry);
        var progress = ledger.Progress;
        if (progress is not null && (progress.Scope != Scope || progress.Count < 0 || progress.Revision < 0))
            return Unavailable(null);
        if (!ledger.Verified || progress is null || ledger.AwardMask27 is not uint mask ||
            ledger.OverflowAwardCount is not long overflow || entry.Availability == PromotionAvailability.Invalid)
            return Unavailable(progress);
        var rules = entry.Configuration.Rules;
        var tiers = rules.Tiers.OrderBy(tier => tier.Sequence).ToArray();
        if (rules.CycleLength != 10 || rules.Cycles != 3 || rules.OverflowFrom != 31 || rules.MaximumPurchases != 5000 ||
            rules.OverflowItemId != 490705415 || rules.OverflowCount != 1 || tiers.Length != 9)
            return Unavailable(progress);
        for (int index = 0; index < tiers.Length; index++)
            if (tiers[index].Sequence != index + 2 || tiers[index].ItemId != 490705441 + index || tiers[index].Count != 1)
                return Unavailable(progress);
        try
        {
            var rewards = ImmutableArray.CreateBuilder<PromotionPageReward>(9);
            foreach (var tier in tiers) rewards.Add(Reward(tier.ItemId, tier.Count));
            var overflowReward = Reward(rules.OverflowItemId, rules.OverflowCount);
            return new(Scope.Campaign, Scope.Period, Scope.RulesVersion, Scope.Realm, Scope.Group, catalog.Revision,
                progress.Revision, entry.Availability == PromotionAvailability.Disabled ? "disabled" : "ready", progress.Count,
                rules.MaximumPurchases, mask, overflow, ttlMs, rewards.ToImmutable(), overflowReward);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
        {
            // 名称缺失不能造占位奖项或清零已读进度；只返回完整unavailable。
            return Unavailable(progress);
        }
    }

    PromotionPageReward Reward(int itemId, int count)
    {
        var template = item(itemId);
        string name = template.Name;
        if (template.Id != itemId || string.IsNullOrWhiteSpace(name) || name[0] == (char)0xfeff ||
            name.Any(value => value <= (char)0x1f || value is >= (char)0x7f and <= (char)0x9f))
            throw new InvalidDataException("活动奖励模板名称无效");
        try
        {
            if (StrictUtf8.GetByteCount(name) > 192) throw new InvalidDataException("活动奖励模板名称超过协议边界");
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("活动奖励模板名称不是合法Unicode", error);
        }
        return new(itemId, count, name);
    }
}
