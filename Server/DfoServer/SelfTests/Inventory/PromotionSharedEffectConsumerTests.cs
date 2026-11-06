using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionSharedEffectConsumerTests
{
    const int RealSource = 490010274, Source = 880008001, EquipmentLeaf = 880008002,
        UnknownLeaf = 880008003, InvokedPool = 880008004, BadPool = 880008005;

    public static void RunResources(ItemResources resources)
    {
        That(resources.Items.SharedEffect(RealSource) is
            { Outcomes: [{ Weight: 97000, BoosterId: -1 }, { Weight: 3000, BoosterId: 490010280 }] },
            "真实SharedEffect保留空结果和直接调用结果奖池的完整定义");
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        var rules = original.Rules with
        {
            Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                ? tier with { ItemId = RealSource, Count = 1 } : tier).ToImmutableArray()
        };
        var catalog = ServerRuntime.LoadCashShopPromotions(resources,
            JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
        var entry = catalog.Activities.Single();
        // 保留完整真实图；490010277强化券和所有子盒后继均由现有正式消费者证明。
        That(entry.Availability == PromotionAvailability.Active,
            "正式loader连接490010274完整真实SharedEffect奖励图: " + entry.Error);
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        Fixture New() => new(resources);
        That(New().Validate() is null, "直接调用奖池，不要求中间礼包存在物品模板");
        var empty = New();
        empty.Effect = empty.Effect! with { Outcomes = [new(0, 1, -1), new(1, 1, 0)] };
        That(empty.Validate() is null, "非正数引用为真实合法空结果");
        var zero = New();
        zero.Effect = zero.Effect! with { Outcomes = [new(0, 1, InvokedPool), new(1, 0, BadPool)] };
        That(zero.Validate() is null, "零权重后继不实际可达");
        var absent = New(); absent.Effect = null;
        Reject(absent, "shared_effect_definition", "缺严格typed定义拒绝");
        var forbidden = New(); forbidden.Template = forbidden.Template with { UseForbidden = true };
        Reject(forbidden, "shared_effect_definition", "禁用来源拒绝");
        var wrongKind = New(); wrongKind.Template = wrongKind.Template with { Kind = ItemKind.Equipment };
        Reject(wrongKind, "shared_effect_definition", "非消耗品来源拒绝");
        foreach (var outcomes in new ImmutableArray<SharedEffectReward>[]
        {
            default, [], [new(0, -1, -1)], [new(0, 0, -1)], [new(1, 1, -1)]
        })
        {
            var malformed = New(); malformed.Effect = malformed.Effect! with { Outcomes = outcomes };
            Reject(malformed, "shared_effect_definition", "空、负权重、全零或非连续索引定义拒绝");
        }
        var later = New();
        later.Effect = later.Effect! with { Outcomes = [new(0, 1, InvokedPool), new(1, 1, BadPool)] };
        Reject(later, "shared_effect_pool_definition:" + BadPool, "正权重后续奖池仍完整检查");
        var pending = New(); pending.Pool = pending.Pool with { PendingRule = "未连接结果规则" };
        Reject(pending, "shared_effect_pool_definition", "结果礼包不可用拒绝");
        foreach (var mode in new BoosterDefinition[]
        {
            new([new(1, [new(EquipmentLeaf, 1, 1)])], Lottery: true),
            new([], Selection: new(1, [])),
            new([], PackageRewards: [new(EquipmentLeaf, 1, 1)]),
            new([]) { RuneBox = (0, 1) }
        })
        {
            var mismatch = New(); mismatch.Pool = mode;
            Reject(mismatch, "shared_effect_pool_definition", "CMD251不能借其它开箱入口放行");
        }
        var final = New();
        final.Pool = new([new(1, [new(EquipmentLeaf, 1, 1), new(UnknownLeaf, 1, 1)])]);
        Reject(final, "consumer_not_connected:[etc] 0", "第二个真实最终奖励仍展开其消费者");
        var cycle = New(); cycle.Pool = new([new(1, [new(Source, 1, 1)])]);
        Reject(cycle, "free_reward_cycle", "免费后继归原SharedEffect来源，不能藏在中间礼包节点");
        var unrelated = New(); unrelated.Template = unrelated.Template with { StackableType = "[etc] 0" };
        bool read = false;
        unrelated.Effects[Source] = new(() =>
        {
            read = true;
            throw new InvalidDataException("无关类型不得读取SharedEffect");
        });
        Reject(unrelated, "consumer_not_connected:[etc] 0", "其它类型仍保持原拒绝");
        That(!read, "非SharedEffect物品不查询SharedEffect typed资源");
    }

    static void Reject(Fixture fixture, string reason, string label)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains(reason, StringComparison.Ordinal), label + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly PromotionRules rules;
        readonly ConcurrentDictionary<int, BoosterDefinition> pools;
        public ConcurrentDictionary<int, Lazy<SharedEffectConsumableDefinition?>> Effects { get; }
        public ItemTemplate Template { get; set; }
        public SharedEffectConsumableDefinition? Effect
        {
            get => Effects[Source].Value;
            set => Effects[Source] = new(() => value);
        }
        public BoosterDefinition Pool
        {
            get => pools[InvokedPool];
            set => pools[InvokedPool] = value;
        }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>
            {
                [Source] = "fixture/shared-effect.stk", [UnknownLeaf] = "fixture/unknown.stk"
            }, 100);
            Template = new(Source, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "隔离SharedEffect", "[random reward item] 0");
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            var unknown = Template with { Id = UnknownLeaf, StackableType = "[etc] 0" };
            // 不注册InvokedPool模板：结果是奖池调用，不是中间物品发放。
            items.PrefillTemplates(new[] { Template, equipment, unknown }.Select(item => KeyValuePair.Create(item.Id, item)));
            Effects = Field<ConcurrentDictionary<int, Lazy<SharedEffectConsumableDefinition?>>>(items, "sharedEffects");
            Effect = new(1000, -Source, 0, [new(0, 97, -1), new(1, 3, InvokedPool)]);
            foreach (int id in new[] { Source, UnknownLeaf })
            {
                Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables")[id] = new(() => null);
                Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables")[id] = new(() => null);
                var raw = new StackableFile(id, "stackable/fixture/shared-effect.stk", new("[etc]", [0]),
                    100, null, null, 1, null, []);
                Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions")[id] = new(() => raw);
            }
            boosters = new(archive, new Dictionary<int, string>());
            pools = Field<ConcurrentDictionary<int, BoosterDefinition>>(boosters, "definitions");
            Pool = new([new(1, [new(EquipmentLeaf, 1, 1)])]);
            avatars = new(_ => throw new InvalidDataException("隔离SharedEffect不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("shared-effect-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public string? Validate()
        {
            items.PrefillTemplates([KeyValuePair.Create(Source, Template)]);
            return new PromotionConsumerValidation(items, boosters, avatars, shop,
                resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
                resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
                resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
        }
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
