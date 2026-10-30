using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfoServer.GameWorld.Items;

internal sealed record PromotionConfiguration(PromotionRules Rules, bool Enabled = false,
    string DisabledReason = "奖励消费者和最终使用、SQL邮件链尚未闭环",
    string RuleKind = "pilot", PromotionCarryForward? CarryForward = null);
internal enum PromotionAvailability { Disabled, Active, Invalid }
internal sealed record PromotionCatalogEntry(PromotionConfiguration Configuration, string? Error)
{
    public PromotionPolicy? Policy { get; init; }
    public PromotionAvailability Availability => !Configuration.Enabled ? PromotionAvailability.Disabled :
        Error is null ? PromotionAvailability.Active : PromotionAvailability.Invalid;
}

internal sealed class CashShopPromotionCatalog
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
    static readonly object revisionGate = new();
    static long nextRevision;
    readonly FrozenDictionary<int, PromotionCatalogEntry> products;

    public ImmutableArray<PromotionCatalogEntry> Activities { get; }
    // 只用于本进程捕获/复验，不作为规则身份或持久计数版本。
    public long Revision { get; }

    CashShopPromotionCatalog(ImmutableArray<PromotionCatalogEntry> activities,
        Dictionary<int, PromotionCatalogEntry> products)
    {
        Activities = activities;
        this.products = products.ToFrozenDictionary();
        lock (revisionGate)
            Revision = checked(++nextRevision);
    }

    public static CashShopPromotionCatalog Parse(string json, CashShopCatalog shop,
        Func<int, ItemTemplate> item, Func<PromotionRules, string?>? validateConsumers = null)
    {
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(item);
        PromotionConfiguration[] configurations;
        try
        {
            configurations = JsonSerializer.Deserialize<PromotionConfiguration[]>(json, JsonOptions)
                ?? throw new InvalidDataException("促销配置不得为null");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("促销配置格式无效", exception);
        }
        if (configurations.Length > PromotionRuleValidation.MaximumActivities)
            throw new InvalidDataException("促销活动数不得超过16");
        var activities = ImmutableArray.CreateBuilder<PromotionCatalogEntry>();
        var products = new Dictionary<int, PromotionCatalogEntry>();
        var scopes = new HashSet<PromotionScope>();
        foreach (var configuration in configurations)
        {
            // 身份或映射不可恢复时拒绝整个目录，调用方不得吞掉错误并回落普通购买。
            if (configuration?.Rules is not { } rules || !scopes.Add(rules.Scope) ||
                rules.Products.IsDefaultOrEmpty || rules.Products.Any(product => product is null || product.CommodityId <= 0))
                throw new InvalidDataException("促销身份重复或商品拒绝范围无法恢复");
            string? error = null;
            PromotionPolicy? policy = null;
            try
            {
                policy = PromotionPolicy.FromConfiguration(configuration);
                if (configuration.Enabled)
                {
                    ValidateResources(rules, shop, item);
                    // 可信装配方的实际消费者核验结果；此门槛不能由运营JSON声明替代。
                    error = validateConsumers is null ? "促销奖励消费者与最终使用链尚未核实" : validateConsumers(rules);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or ArgumentException)
            {
                error = exception.Message;
            }
            var entry = new PromotionCatalogEntry(configuration, error) { Policy = policy };
            activities.Add(entry);
            foreach (var product in rules.Products)
            {
                if (!products.TryAdd(product.CommodityId, entry))
                    throw new InvalidDataException("多个促销映射同一商品，无法确定累计归属");
            }
        }
        return new(activities.ToImmutable(), products);
    }

    public PromotionCatalogEntry? Product(int commodityId) => products.GetValueOrDefault(commodityId);

    public bool IsCurrent(long revision, PromotionScope scope) => revision == Revision &&
        Activities.Any(entry => entry.Availability == PromotionAvailability.Active && entry.Configuration.Rules.Scope == scope);

    // 供后续持久化比较同身份规则；排序消除JSON声明顺序，运行开关和revision均不入快照。
    public static string RuleSnapshot(PromotionRules rules)
    {
        PromotionRuleValidation.Validate(rules);
        return CanonicalSnapshot(rules);
    }

    public static string RuleSnapshot(PromotionRules rules, PromotionPolicy policy)
    {
        policy.Validate(rules);
        return CanonicalSnapshot(rules);
    }

    internal static string CanonicalSnapshot(PromotionRules rules) => JsonSerializer.Serialize(rules with
        {
            Products = rules.Products.OrderBy(product => product.CommodityId).ToImmutableArray(),
            Tiers = rules.Tiers.OrderBy(tier => tier.Sequence).ToImmutableArray()
        }, JsonOptions);

    public static CashShopPromotionCatalog Load(string path, CashShopCatalog shop,
        Func<int, ItemTemplate> item, Func<PromotionRules, string?>? validateConsumers = null) =>
        Parse(File.ReadAllText(path), shop, item, validateConsumers);

    static void ValidateResources(PromotionRules rules, CashShopCatalog shop, Func<int, ItemTemplate> item)
    {
        foreach (var product in rules.Products)
        {
            var actual = shop.Product(product.CommodityId);
            if (actual is null || actual.ItemId != product.ItemId || actual.Count != 1 || item(product.ItemId).Id != product.ItemId)
                throw new InvalidDataException("促销商品与实际商城资源映射不符");
        }
        foreach (int id in rules.Tiers.Select(tier => tier.ItemId).Append(rules.OverflowItemId).Distinct())
        {
            var template = item(id);
            // Count恒1：任何有效堆叠上限都只产生一个完整实例，每车至多64附件、7封邮件。
            if (template.Id != id || template.StackLimit < 1)
                throw new InvalidDataException("促销奖励模板或实际堆叠能力无效");
        }
    }
}
