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

internal static class PromotionFatigueConsumerTests
{
    const int Source = 880003101, EquipmentLeaf = 880003102;
    // 两项均为真实活动奖励叶，由疲劳动作消费者处理。
    static readonly int[] RealSources = [416, 2749834];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealSources)
        {
            var fatigue = resources.Items.FatigueConsumable(id);
            That(fatigue is { Item.Id: var itemId, Amount: > 0 } && itemId == id,
                "真实疲劳恢复叶连接typed [add fatigue]消费者 " + id);
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实疲劳恢复消费者 " + id + ": " + entry.Error);
        }
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        var real = resources.Items.FatigueConsumable(416)
            ?? throw new InvalidDataException("缺少416真实疲劳定义");
        Fixture New() => new(resources, real);
        var valid = New();
        That(valid.Validate() is null, "隔离疲劳typed正例经完整消费者图核验");

        var missing = New();
        missing.Fatigues[Source] = new(() => null);
        Reject(missing, "consumer_not_connected:", "疲劳标签没有typed定义不能统放");

        var wrongTemplate = New();
        var original = wrongTemplate.Fatigues[Source].Value!;
        wrongTemplate.Fatigues[Source] = new(() => original with
            { Item = original.Item with { Name = "同ID异源模板" } });
        Reject(wrongTemplate, "fatigue_consumable_definition", "同ID异模板仍拒绝");

        var wrongKind = new Fixture(resources, real, item => item with { Kind = ItemKind.Material });
        Reject(wrongKind, "fatigue_consumable_definition", "模板与typed一致但Kind为材料仍拒绝");
        var forbidden = new Fixture(resources, real, item => item with { UseForbidden = true });
        Reject(forbidden, "fatigue_consumable_definition", "模板与typed一致但禁用仍拒绝");

        var zero = New();
        var positive = zero.Fatigues[Source].Value!;
        zero.Fatigues[Source] = new(() => positive with { Amount = 0 });
        Reject(zero, "fatigue_consumable_definition", "恢复量为零不能接通消费者");
        var defaultPlaces = New();
        var places = defaultPlaces.Fatigues[Source].Value!;
        defaultPlaces.Fatigues[Source] = new(() => places with { Places = default });
        Reject(defaultPlaces, "fatigue_consumable_definition", "地点数组未初始化不能接通消费者");

        var unknown = New();
        var raw = unknown.Stackables[Source].Value;
        unknown.Stackables[Source] = new(() => raw with
            { ActionDeclaration = new("[promotion fatigue fixture unknown]", [1]) });
        bool fatigueRead = false;
        unknown.Fatigues[Source] = new(() =>
        {
            fatigueRead = true;
            throw new InvalidDataException("非疲劳标签不应读取疲劳定义");
        });
        Reject(unknown, "consumer_not_connected:", "未知etc动作保持原消费者失败");
        That(!fatigueRead, "非add fatigue标签不会求值疲劳getter或访问虚拟路径");
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
        public ConcurrentDictionary<int, Lazy<FatigueConsumableDefinition?>> Fatigues { get; }
        public ConcurrentDictionary<int, Lazy<StackableFile>> Stackables { get; }

        public Fixture(ItemResources resources, FatigueConsumableDefinition real,
            Func<ItemTemplate, ItemTemplate>? changeTemplate = null)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            // 所有虚拟源的读取路径均由本fixture的lazy隔离，不修改共享资源缓存。
            items = new(archive, new Dictionary<int, string>(),
                new Dictionary<int, string> { [Source] = "fixture/fatigue.stk" }, 100);
            var template = real.Item with { Id = Source };
            if (changeTemplate is not null) template = changeTemplate(template);
            var definition = real with { Item = template };
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            items.PrefillTemplates(new[] { template, equipment }.Select(item => KeyValuePair.Create(item.Id, item)));
            Fatigues = Field<ConcurrentDictionary<int, Lazy<FatigueConsumableDefinition?>>>(items, "fatigueConsumables");
            Fatigues[Source] = new(() => definition);
            // 未知etc也复用同一原始类型，消费者资格只来自实际typed定义。
            var raw = new StackableFile(Source, "stackable/fixture/fatigue.stk", new("[etc]", [0]),
                template.StackLimit, null, null, 1, null, [])
            {
                ActionDeclaration = new("[add fatigue]", [definition.Amount]),
                HasUseActionPacket = true,
                DeclaredUseActionPacket = 1
            };
            Stackables = Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions");
            Stackables[Source] = new(() => raw);
            Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables")[Source] = new(() => null);
            Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables")[Source] = new(() => null);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离疲劳用例不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("fatigue-fixture", "period", "v1", "default", "package"),
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
