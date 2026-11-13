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

internal static class PromotionCreatureRenameConsumerTests
{
    const int Source = 880010001, EquipmentLeaf = 880010002;

    public static void RunResources(ItemResources resources)
    {
        That(resources.Items.Get(25) is { Kind: ItemKind.CreatureConsumable, StackableType: "[creature] 1" } &&
            resources.Creatures.RenameCard(25), "真实25具有宠物换名卡的同源消费者合同");
        var original = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())![0];
        var rules = original.Rules with { Tiers = [original.Rules.Tiers[0] with { ItemId = 25, Count = 1 }] };
        var activity = ServerRuntime.LoadCashShopPromotions(resources,
            JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } })).Activities.Single();
        That(activity.Availability == PromotionAvailability.Active,
            "正式活动loader接通真实宠物换名卡: " + activity.Error);

        var valid = new Fixture(resources);
        That(valid.Validate() is null && valid.Reads == 1, "换名卡沿实际目录识别且不读取喂食冷却合同");
        var originalQualifications = new Fixture(resources);
        originalQualifications.Template = originalQualifications.Template with
        { UseForbidden = true, Jobs = 0, MinimumLevel = 200, MaximumLevel = 1 };
        That(originalQualifications.Validate() is null, "不向原改名消费者新增角色职业、等级和普通使用门禁");
        foreach (string script in new[]
        {
            "[stackable type]\n`[creature]` 1\n[action type]\n`[creature use item]` 1 50\n[/action type]",
            "[stackable type]\n`[creature]` 1\n[action type]\n`[creature use item]` 2 1\n[/action type]",
            "[stackable type]\n`[creature]` 1\n[action type]\n`[creature use item]` 3 50\n[/action type]",
            "[stackable type]\n`[creature]` 1\n[action type]\n`[unknown]` 0\n[/action type]",
            "[stackable type]\n`[creature]` 2"
        })
        {
            var unrelated = new Fixture(resources) { Script = script };
            Reject(unrelated, "consumer_not_connected:[creature] 1", "经验、等级和其它声明不冒充换名卡");
        }
        var wrongOwner = new Fixture(resources);
        wrongOwner.Template = wrongOwner.Template with { Kind = ItemKind.Consumable };
        Reject(wrongOwner, "creature_rename_definition", "换名卡必须属于真实Pet页消耗品owner");
        foreach (string type in new[] { "[creature] 2", "[etc] 0" })
        {
            var otherType = new Fixture(resources);
            otherType.Template = otherType.Template with { StackableType = type };
            Reject(otherType, "consumer_not_connected:" + type, "其它模板类型不借换名卡目录放行");
            That(otherType.Reads == 0, "非换名卡类型不读取改名资源");
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
        readonly CreatureCatalog creatures;
        readonly PromotionRules rules;
        public ItemTemplate Template { get; set; } = new(Source, ItemKind.CreatureConsumable, 0, 1, 0, 100,
            0, 0, 0, "[free]", ushort.MaxValue, 0, "隔离宠物换名卡", "[creature] 1");
        public string Script { get; set; } = "[stackable type]\n`[creature]` 1";
        public int Reads { get; private set; }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string> { [Source] = "fixture/rename.stk" }, 100);
            items.PrefillTemplates([KeyValuePair.Create(Source, Template), KeyValuePair.Create(EquipmentLeaf,
                new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 0,
                    "[free]", ushort.MaxValue, 0, "合法装备控制叶"))]);
            Field<ConcurrentDictionary<int, Lazy<ActionConsumableDefinition?>>>(items, "actionConsumables")[Source] = new(() => null);
            Field<ConcurrentDictionary<int, Lazy<LevelUpByTableDefinition?>>>(items, "levelUpTables")[Source] = new(() => null);
            var raw = new StackableFile(Source, "stackable/fixture/rename.stk", new("[etc]", [0]), 100, null, null, 1, null, []);
            Field<ConcurrentDictionary<int, Lazy<StackableFile>>>(items, "stackableDefinitions")[Source] = new(() => raw);
            creatures = new(path =>
            {
                if (path != "stackable/fixture/rename.stk") throw new InvalidDataException("隔离改名意外读取: " + path);
                Reads++;
                return Script;
            }, _ => throw new InvalidDataException("隔离改名不应读取CRE"), new Dictionary<int, string>(),
                new Dictionary<int, string> { [Source] = "fixture/rename.stk" }, new Dictionary<int, string>(), null);
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离改名不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("creature-rename-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public string? Validate()
        {
            items.PrefillTemplates([KeyValuePair.Create(Source, Template)]);
            return new PromotionConsumerValidation(items, boosters, avatars, shop,
                resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs, creatures,
                resources.CollectBoxes, resources.LimitCubes, resources.Enchantments, resources.Enhancements,
                resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
        }
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
