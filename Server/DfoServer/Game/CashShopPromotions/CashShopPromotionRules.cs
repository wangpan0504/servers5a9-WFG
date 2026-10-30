using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.CashShopPromotions;

internal sealed record PromotionProgress(PromotionScope Scope, long Count, long Revision);
internal sealed record PromotionAward(long PurchaseNumber, int ItemId, int Count);

internal static class CashShopPromotionRules
{
    public static ImmutableArray<PromotionAward> Settle(PromotionRules rules, long before, int boughtUnits) =>
        Settle(rules, before, boughtUnits, PromotionPolicy.Pilot(rules));

    public static ImmutableArray<PromotionAward> Settle(PromotionRules rules, long before, int boughtUnits, PromotionPolicy policy)
    {
        policy.Validate(rules);
        if (before < 0 || before > rules.MaximumPurchases || boughtUnits is < 1 or > PromotionRuleValidation.MaximumCartLines)
            throw new ArgumentOutOfRangeException(nameof(before), "促销累计或单车套数无效");
        long after = checked(before + boughtUnits);
        if (after > rules.MaximumPurchases)
            throw new ArgumentOutOfRangeException(nameof(boughtUnits), "本次购买超出促销累计上限");
        var awards = ImmutableArray.CreateBuilder<PromotionAward>();
        for (int offset = 1; offset <= boughtUnits; offset++)
        {
            long number = checked(before + offset);
            if (number >= rules.OverflowFrom)
            {
                awards.Add(new(number, rules.OverflowItemId, rules.OverflowCount));
                continue;
            }
            int sequence = checked((int)((number - 1) % rules.CycleLength + 1));
            long round = (number - 1) / rules.CycleLength;
            if (round >= rules.Cycles)
                continue;
            foreach (var tier in rules.Tiers)
            {
                if (tier.Sequence == sequence)
                    awards.Add(new(number, tier.ItemId, tier.Count));
            }
        }
        return awards.ToImmutable();
    }
}
