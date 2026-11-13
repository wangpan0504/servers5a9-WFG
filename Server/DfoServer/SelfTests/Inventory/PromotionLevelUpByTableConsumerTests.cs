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

internal static class PromotionLevelUpByTableConsumerTests
{
    const int Plain = 10158963, Source = 880003001, EquipmentLeaf = 880003002;
    static readonly int[] RealSources =
    [
        Plain, 10303200, 490704662, 490704541, 490704508,
        490703949, 490703678, 490703677, 490702738
    ];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealSources)
        {
            That(resources.Items.LevelUpByTable(id) is not null &&
                resources.Items.LevelUpTicket(id) is null && resources.Items.ExperienceConsumable(id) is null,
                "真实按表升级来源归属typed表消费者，不借418或普通经验增量 " + id);
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实按表升级消费者 " + id + ": " + entry.Error);
        }
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        var real = resources.Items.LevelUpByTable(Plain)!;
        Fixture New() => new(resources, real);
        var valid = New();
        That(valid.Validate() is null, "隔离typed按表升级正例仍经完整消费者图核验");

        var wrongId = New();
        var original = wrongId.Tables[Source].Value!;
        wrongId.Tables[Source] = new(() => original with { Item = original.Item with { Id = Source + 10 } });
        Reject(wrongId, "level_up_by_table_definition", "typed Item源ID不符");
        var wrongTemplate = New();
        var sameId = wrongTemplate.Tables[Source].Value!;
        wrongTemplate.Tables[Source] = new(() => sameId with { Item = sameId.Item with { Name = "异源模板" } });
        Reject(wrongTemplate, "level_up_by_table_definition", "typed Item同ID但模板不符");

        var unrecognized = New();
        unrecognized.Tables[Source] = new(() => null);
        Reject(unrecognized, "consumer_not_connected:", "普通etc没有typed定义仍拒绝，不按类型统放");
    }

    static void Reject(Fixture fixture, string reason, string label)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains(Source.ToString(), StringComparison.Ordinal) &&
            error.Contains(reason, StringComparison.Ordinal), label + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly PromotionRules rules;
        public ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>> Tables { get; }

        public Fixture(ItemResources resources, LevelUpByTableDefinition real)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            // getter先检查stackable映射，随后命中隔离lazy；此虚拟路径不应被读取。
            items = new(archive, new Dictionary<int, string>(),
                new Dictionary<int, string> { [Source] = "fixture/level-up-table.stk" }, 100);
            var definition = real with { Item = real.Item with { Id = Source } };
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            items.PrefillTemplates(new[] { definition.Item, equipment }.Select(item => KeyValuePair.Create(item.Id, item)));
            Tables = Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables");
            Tables[Source] = new(() => definition);
            // CashShopCouponKey与ActionConsumable在typed表分支之前也可能访问该隔离源。
            var raw = new StackableFile(Source, "stackable/fixture/level-up-table.stk", new("[etc]", [0]),
                definition.Item.StackLimit, null, null, 1, null, []);
            Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions")[Source] = new(() => raw);
            Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables")[Source] = new(() => null);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离等级表用例不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("level-table-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public string? Validate() => new PromotionConsumerValidation(items, boosters, avatars, shop,
            resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
            resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
            resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
    }

    // 仅修改隔离ItemCatalog，正式resources与共享缓存不变。
    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
