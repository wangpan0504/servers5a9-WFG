namespace DfoServer.GameWorld.Items;

internal sealed record PromotionCarryForward(PromotionScope FromScope, string FromRulesSnapshotSha256);

// 仅严格服务端配置工厂产生；请求、累计数值和规则版本本身都不能授予常驻能力。
internal sealed class PromotionPolicy
{
    readonly string boundSnapshot;
    public bool IsPermanent { get; }
    public PromotionScope Scope { get; }
    public PromotionScope CounterScope { get; }
    public PromotionRules? SourceRules { get; }
    public string? SourceSnapshot { get; }

    PromotionPolicy(PromotionRules rules, PromotionRules? source)
    {
        IsPermanent = source is not null;
        Scope = rules.Scope;
        CounterScope = source?.Scope ?? rules.Scope;
        SourceRules = source;
        SourceSnapshot = source is null ? null : CashShopPromotionCatalog.RuleSnapshot(source);
        boundSnapshot = CashShopPromotionCatalog.CanonicalSnapshot(rules);
    }

    public static PromotionPolicy Pilot(PromotionRules rules)
    {
        PromotionRuleValidation.Validate(rules);
        return new(rules, null);
    }

    public static PromotionPolicy FromConfiguration(PromotionConfiguration configuration)
    {
        if (configuration.RuleKind == "pilot" && configuration.CarryForward is null)
            return Pilot(configuration.Rules);
        throw new InvalidDataException("促销仅允许原有界规则；常驻续接不能获得新购授权");
    }

    public void Validate(PromotionRules rules)
    {
        PromotionRuleValidation.ValidateShape(rules, IsPermanent ? long.MaxValue : PromotionRuleValidation.MaximumPilotPurchases);
        if (CashShopPromotionCatalog.CanonicalSnapshot(rules) != boundSnapshot)
            throw new InvalidDataException("促销规则与冻结策略不一致");
    }

}
