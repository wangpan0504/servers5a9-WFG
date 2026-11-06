using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Creatures;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionCreatureFeedConsumerTests
{
    const int RealSource = 24, Source = 880009001, EquipmentLeaf = 880009002;

    public static void RunResources(ItemResources resources)
    {
        That(resources.Items.Get(RealSource) is { Kind: ItemKind.CreatureConsumable, StackableType: "[feed] 1" } &&
            resources.Creatures.Feed(RealSource) is { ItemId: RealSource },
            "真实24保留宠物消耗品模板和正式typed饲料定义");
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
        That(entry.Availability == PromotionAvailability.Active,
            "正式loader连接24完整真实普通宠物饲料消费者图: " + entry.Error);
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        Fixture New() => new(resources);
        var valid = New();
        That(valid.Validate() is null && valid.ScriptReads == 1,
            "合法typed饲料接通并读取同源缺省冷却组");
        var explicitGroup = New(); explicitGroup.Script += "\n[cooltime group]\n0";
        That(explicitGroup.Validate() is null, "显式零组保持真实共享冷却资格");
        var badGroup = New(); badGroup.Script += "\n[cooltime group]\n-1";
        Reject(badGroup, "宠物道具冷却组不能为负数", "合法Feed定义不能掩盖实际Session先读取的坏冷却组");
        var absent = New(); absent.Feeds.Clear(); absent.Script = "[stackable type]\n`[feed]` 2";
        Reject(absent, "creature_feed_definition", "同族其它参数没有普通饲料typed定义仍拒绝");
        var malformed = New(); malformed.Feeds.Clear();
        malformed.Script = "[stackable type]\n`[feed]` 1\n[int data]\n300 -20\n[/int data]";
        Reject(malformed, "宠物饲料持续时间、集气修正或饱食恢复声明无效", "真实目录拒绝缺项原始定义，不能按feed标签统放");
        var wrongId = New(); wrongId.Definition = wrongId.Definition with { ItemId = Source + 1 };
        Reject(wrongId, "creature_feed_definition", "typed定义来源编号不符拒绝");
        var wrongKind = New(); wrongKind.Template = wrongKind.Template with { Kind = ItemKind.Consumable };
        Reject(wrongKind, "creature_feed_definition", "普通饲料必须沿Pet页宠物消耗品来源");
        foreach (var invalid in new Func<CreatureFeedDefinition, CreatureFeedDefinition>[]
        {
            row => row with { MinimumLevel = -1 }, row => row with { CoolTime = -1 },
            row => row with { Satiety = 0 }, row => row with { Satiety = 101 },
            row => row with { EffectSeconds = -1 }, row => row with { SkillRecoveryRate = -101 },
            row => row with { SkillRecoveryRate = 1001 }
        })
        {
            var fixture = New(); fixture.Definition = invalid(fixture.Definition);
            Reject(fixture, "creature_feed_definition", "typed缓存仍满足真实饲料数值边界");
        }
        var limits = New();
        limits.Definition = limits.Definition with { MinimumLevel = 0, CoolTime = 0, Satiety = 100,
            EffectSeconds = 0, SkillRecoveryRate = -100 };
        That(limits.Validate() is null, "合法零持续、零冷却及负集气修正不误拒绝");
        var sourceFields = New();
        sourceFields.Template = sourceFields.Template with { UseForbidden = true, Jobs = 0,
            MinimumLevel = 200, MaximumLevel = 1 };
        That(sourceFields.Validate() is null, "不向既有宠物喂养消费者擅加角色职业和等级门禁");
        foreach (string type in new[] { "[feed] 2", "[creature] 1", "[creature expitem] 0" })
        {
            var unrelated = New(); unrelated.Template = unrelated.Template with { StackableType = type };
            Reject(unrelated, "consumer_not_connected:" + type, "其它宠物道具不借普通饲料owner放行");
            // 同族改名消费者已独立接通，会读取原脚本确认用途；不能再要求它完全不读源。
            That(unrelated.ScriptReads == (type == "[creature] 1" ? 1 : 0),
                "非普通饲料不借Feed放行，改名族仅按自身消费者读取同源脚本");
        }
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
        readonly CreatureCatalog creatures;
        public ItemTemplate Template { get; set; }
        public string Script { get; set; } = "[stackable type]\n`[feed]` 1\n[int data]\n300 -20 30\n[/int data]";
        public int ScriptReads { get; private set; }
        public ConcurrentDictionary<int, CreatureFeedDefinition> Feeds { get; }
        public CreatureFeedDefinition Definition
        {
            get => Feeds[Source];
            set => Feeds[Source] = value;
        }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(),
                new Dictionary<int, string> { [Source] = "fixture/feed.stk" }, 100);
            Template = new(Source, ItemKind.CreatureConsumable, 0, 1, 0, 100, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "隔离普通宠物饲料", "[feed] 1");
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            items.PrefillTemplates([KeyValuePair.Create(Source, Template), KeyValuePair.Create(EquipmentLeaf, equipment)]);
            // 非feed负控制仍经过原消费者查询，隔离缓存避免读取虚拟路径。
            Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables")[Source] = new(() => null);
            Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables")[Source] = new(() => null);
            var raw = new StackableFile(Source, "stackable/fixture/feed.stk", new("[etc]", [0]),
                100, null, null, 1, null, []);
            Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions")[Source] = new(() => raw);
            creatures = new(path =>
            {
                if (path != "stackable/fixture/feed.stk") throw new InvalidDataException("隔离饲料意外读取: " + path);
                ScriptReads++;
                return Script;
            }, _ => throw new InvalidDataException("隔离饲料不应读取宠物CRE"),
                new Dictionary<int, string>(), new Dictionary<int, string> { [Source] = "fixture/feed.stk" },
                new Dictionary<int, string>(), null);
            Feeds = Field<ConcurrentDictionary<int, CreatureFeedDefinition>>(creatures, "feeds");
            Definition = new(Source, 1, 10000, 30, 300, -20);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离饲料不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("creature-feed-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public string? Validate()
        {
            items.PrefillTemplates([KeyValuePair.Create(Source, Template)]);
            return new PromotionConsumerValidation(items, boosters, avatars, shop,
                resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
                creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
                resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
        }
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
