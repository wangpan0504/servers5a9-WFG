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

internal static class PromotionDimensionEntryConsumerTests
{
    const int RealSource = 10158125, Source = 880005001, EquipmentLeaf = 880005002;
    static readonly int[] RealSources =
    [
        RealSource, 10158126, 490008217, 10158692, 10161066, 490003748, 490009451
    ];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealSources)
        {
            That(resources.Items.ActionConsumable(id) is
                { Label: "[add dimension dungeon enter count]", Parameters: [1 or 5] },
                "真实异界追加券保持正式typed动作及1或5次资源参数 " + id);
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实异界追加券账本消费者 " + id + ": " + entry.Error);
        }
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        var real = resources.Items.ActionConsumable(RealSource)!;
        Fixture New() => new(resources, real);
        var valid = New();
        That(valid.Validate() is null, "隔离typed异界追加券正常连接，不按waste标签统放");
        var middle = New();
        var middleDefinition = middle.Actions[Source].Value!;
        middle.Actions[Source] = new(() => middleDefinition with { Parameters = [3] });
        That(middle.Validate() is null, "原消费者范围内的3次券同样识别，不按当前1或5次样本建白名单");
        var absent = New();
        absent.Actions[Source] = new(() => null);
        Reject(absent, "缺严格typed定义仍拒绝");
        var wrongId = New();
        var idDefinition = wrongId.Actions[Source].Value!;
        wrongId.Actions[Source] = new(() => idDefinition with { Item = idDefinition.Item with { Id = Source + 10 } });
        Reject(wrongId, "typed定义异源ID拒绝");
        var wrongTemplate = New();
        var templateDefinition = wrongTemplate.Actions[Source].Value!;
        wrongTemplate.Actions[Source] = new(() => templateDefinition with
            { Item = templateDefinition.Item with { Name = "异源模板" } });
        Reject(wrongTemplate, "typed定义同ID异模板拒绝");
        foreach (var parameters in new ImmutableArray<int>[] { [0], [6], [1, 2] })
        {
            var malformed = New();
            var definition = malformed.Actions[Source].Value!;
            malformed.Actions[Source] = new(() => definition with { Parameters = parameters });
            Reject(malformed, "同label但不符合真实消费者单项1至5参数拒绝");
        }
        var otherAction = New();
        var otherDefinition = otherAction.Actions[Source].Value!;
        otherAction.Actions[Source] = new(() => otherDefinition with { Label = "[repair coupon]", Parameters = [] });
        Reject(otherAction, "其它合法typed动作不借异界owner放行");
    }

    static void Reject(Fixture fixture, string label)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains(Source.ToString(), StringComparison.Ordinal), label + ": " + error);
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly PromotionRules rules;
        public ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>> Actions { get; }

        public Fixture(ItemResources resources, ActionConsumableDefinition real)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(),
                new Dictionary<int, string> { [Source] = "fixture/dimension-entry.stk" }, 100);
            var definition = real with { Item = real.Item with { Id = Source } };
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            items.PrefillTemplates(new[] { definition.Item, equipment }.Select(item => KeyValuePair.Create(item.Id, item)));
            Actions = Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables");
            Actions[Source] = new(() => definition);
            // 隔离来源不属于按表升级消费者，避免读取虚拟资源路径。
            Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables")[Source] = new(() => null);
            // CashShopCouponKey先于typed动作查询raw；只填隔离cache，禁止读取虚拟PVF路径。
            var raw = new StackableFile(Source, "stackable/fixture/dimension-entry.stk", new("[waste]", [0]),
                definition.Item.StackLimit, null, null, 1, null, []);
            Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions")[Source] = new(() => raw);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离异界追加券不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("dimension-entry-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public string? Validate() => new PromotionConsumerValidation(items, boosters, avatars, shop,
            resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
            resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
            resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
