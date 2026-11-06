using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionGuardianGemConsumerTests
{
    const int Equipment = 880013002;
    // 完整守护珠资源族覆盖全部13组、每组4档；生产资格不依赖这份测试列表。
    static readonly int[] RealLeaves =
    [
        90000, 90001, 90002, 90003, 90010, 90011, 90012, 90013,
        90020, 90021, 90022, 90023, 90030, 90031, 90032, 90033,
        90040, 90041, 90042, 90043, 90050, 90051, 90052, 90053,
        90060, 90061, 90062, 90063, 90070, 90071, 90072, 90073,
        90080, 90081, 90082, 90083, 90090, 90091, 90092, 90093,
        90100, 90101, 90102, 90103, 90110, 90111, 90112, 90113,
        90120, 90121, 90122, 90123
    ];

    public static void RunResources(ItemResources resources)
    {
        var real = resources.Items.Get(90000);
        That(real.Id == 90000 && real.Kind == ItemKind.GuardianGem && real.GuardianGemEffects.Count > 0,
            "真实90000具有正式镶嵌Kind与enchant效果身份");
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        var single = original.Rules with
        {
            Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                ? tier with { ItemId = 90000, Count = 1 } : tier).ToImmutableArray()
        };
        Active(single, "真实90000正式loader须连接已有USE_GEM消费者");

        foreach (int id in RealLeaves)
        {
            var item = resources.Items.Get(id);
            That(item.Id == id && item.Kind == ItemKind.GuardianGem && item.GuardianGemEffects.Count > 0,
                "精确52叶保留模板与enchant效果，不按品质代替属性 " + id);
        }
        // 一份内存活动覆盖全部守护珠，保留原商品及溢出奖励，不改运营配置。
        var all = original.Rules with
        {
            CycleLength = RealLeaves.Length, Cycles = 1, OverflowFrom = RealLeaves.Length + 1,
            Tiers = RealLeaves.Select((id, index) => new PromotionTier(index + 1, id, 1)).ToImmutableArray()
        };
        Active(all, "52守护珠真实叶完整正式loader图");
        RunIsolated(resources, real);

        void Active(PromotionRules rules, string label)
        {
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active, label + ": " + entry.Error);
        }
    }

    static void RunIsolated(ItemResources resources, ItemTemplate real)
    {
        Fixture New(int id = 90000, Func<ItemTemplate, ItemTemplate>? change = null) => new(resources, real, id, change);
        That(New().Validate() is null, "隔离守护珠连接真实owner静态资格");
        Reject(New(change: item => item with { Kind = ItemKind.Material }), "同flag gem文字但错Kind拒绝");
        Reject(New(change: item => item with { GuardianGemEffects = [] }), "空效果不能仅按种类放行");
        Reject(New(89999), "零key对应空孔，不能创建为正守护珠");
        That(New(122766).Validate() is null, "signed16正key上界32767可镶嵌");
        Reject(New(122767), "32768不能写为负signed16key");
        Reject(New(int.MaxValue), "大ID不能因减法或类型截断绕过key边界");
        Reject(New(change: item => item with { Id = 90001 }), "catalog别名ID不能借另一模板放行");
        That(New(change: item => item with { UseForbidden = true, Jobs = 0, MinimumLevel = 100, MaximumLevel = 1 }).Validate() is null,
            "不添加正式镶嵌owner不存在的AllowsUse或公会成员静态门槛");
        var whole = New(); whole.ExtraLeaf = real with { Id = 90001, GuardianGemEffects = [] };
        Reject(whole, "完整图后项坏效果不能被首项正常90000掩盖", 90001);
        var lottery = New(); lottery.Lottery = true;
        Reject(lottery, "不借终端镶嵌owner放松普通Lottery的Main容器限制");
    }

    static void Reject(Fixture fixture, string message, int? id = null)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains((id ?? fixture.Id).ToString(), StringComparison.Ordinal), message + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly ItemTemplate gem;
        public int Id { get; }
        public ItemTemplate? ExtraLeaf { get; set; }
        public bool Lottery { get; set; }

        public Fixture(ItemResources resources, ItemTemplate real, int id, Func<ItemTemplate, ItemTemplate>? change)
        {
            this.resources = resources;
            Id = id;
            var archive = (PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(resources.Items)!;
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            gem = real with { Id = id };
            if (change is not null) gem = change(gem);
            var equipment = new ItemTemplate(Equipment, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            items.PrefillTemplates([KeyValuePair.Create(Equipment, equipment), KeyValuePair.Create(id, gem)]);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离守护珠不应读取装扮资源"), new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {Equipment} 1 0 0 100 0 0 0\n");
        }

        public string? Validate()
        {
            const int Box = 880013003;
            var tiers = new List<PromotionTier> { new(2, Lottery ? Box : Id, 1) };
            if (ExtraLeaf is { } extra)
            {
                items.PrefillTemplates([KeyValuePair.Create(extra.Id, extra)]);
                tiers.Add(new(3, extra.Id, 1));
            }
            if (Lottery)
            {
                var source = new ItemTemplate(Box, ItemKind.Consumable, 0, 1, 0, 1000,
                    0, 0, 0, "[free]", ushort.MaxValue, 0, "隔离普通Lottery源", "[booster] 0");
                items.PrefillTemplates([KeyValuePair.Create(Box, source)]);
                var definitions = (System.Collections.Concurrent.ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
                    .GetField("definitions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(boosters)!;
                definitions[Box] = new([new(1, [new(Id, 1, 1)])], Lottery: true);
            }
            var rules = new PromotionRules(new("guardian-gem-fixture", "period", "v1", "default", "package"),
                10, 3, 31, Equipment, 1, 5000, [new(1, Equipment, 1)], tiers.ToImmutableArray());
            return new PromotionConsumerValidation(items, boosters, avatars, shop,
                resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
                resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
                resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
        }
    }
}
