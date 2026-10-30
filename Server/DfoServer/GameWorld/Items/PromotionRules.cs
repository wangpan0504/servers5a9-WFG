using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

internal readonly record struct PromotionScope(string Campaign, string Period,
    string RulesVersion, string Realm, string Group);
internal sealed record PromotionProduct(int CommodityId, int ItemId, int Units);
internal sealed record PromotionTier(int Sequence, int ItemId, int Count);
internal sealed record PromotionRules(PromotionScope Scope, int CycleLength,
    int Cycles, int OverflowFrom, int OverflowItemId, int OverflowCount, long MaximumPurchases,
    ImmutableArray<PromotionProduct> Products, ImmutableArray<PromotionTier> Tiers);

internal static class PromotionRuleValidation
{
    public const int MaximumCartLines = 64;
    public const int MaximumActivities = 16;
    public const int MaximumPilotPurchases = 5000;

    public static void Validate(PromotionRules rules) => ValidateShape(rules, MaximumPilotPurchases);

    public static void Validate(PromotionRules rules, PromotionPolicy policy) => policy.Validate(rules);

    internal static void ValidateShape(PromotionRules rules, long maximum)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var scope = rules.Scope;
        foreach (string key in new[] { scope.Campaign, scope.Period, scope.RulesVersion, scope.Realm, scope.Group })
        {
            if (string.IsNullOrEmpty(key) || key.Length > 32 ||
                key.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
                throw new InvalidDataException("促销身份必须为1至32位ASCII小写字母、数字或短横线");
        }
        try
        {
            if (rules.CycleLength <= 0 || rules.Cycles <= 0 ||
                rules.OverflowFrom != checked(rules.CycleLength * rules.Cycles + 1) ||
                rules.OverflowItemId <= 0 || rules.OverflowCount != 1 ||
                rules.MaximumPurchases < rules.OverflowFrom || rules.MaximumPurchases > maximum)
                throw new InvalidDataException("促销循环、溢出区间或累计上限无效");
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("促销循环区间溢出", exception);
        }
        if (rules.Products.IsDefaultOrEmpty || rules.Tiers.IsDefaultOrEmpty ||
            rules.Tiers.Length > rules.CycleLength)
            throw new InvalidDataException("促销商品或档位集合无效");
        var commodities = new HashSet<int>();
        foreach (var product in rules.Products)
        {
            if (product is null || product.CommodityId <= 0 || product.ItemId <= 0 || product.Units != 1 ||
                !commodities.Add(product.CommodityId))
                throw new InvalidDataException("促销商品重复、身份无效或不是首期单套单位");
        }
        var sequences = new HashSet<int>();
        foreach (var tier in rules.Tiers)
        {
            // 首期每个全局套数至多一个外盒，不能通过数量或重复档位扩大赠礼预算。
            if (tier is null || tier.Sequence < 1 || tier.Sequence > rules.CycleLength || tier.ItemId <= 0 ||
                tier.Count != 1 || !sequences.Add(tier.Sequence))
                throw new InvalidDataException("促销档位重复、身份或数量无效");
        }
    }
}
