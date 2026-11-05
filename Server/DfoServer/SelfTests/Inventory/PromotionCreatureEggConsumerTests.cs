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

internal static class PromotionCreatureEggConsumerTests
{
    const int Source = 880006001, Adult = 880006002, EquipmentLeaf = 880006003;
    static readonly (int Egg, int Output, int Species, int OutputSpecies)[] RealRelations =
    [
        (63022, 63023, 20, 20), (63087, 63088, 52, 52), (63089, 63090, 53, 53),
        (2681356, 2681357, 87, 87), (2681464, 2681465, 90, 90), (2681466, 2681467, 91, 91),
        (2681468, 2681469, 92, 92), (2681470, 2681471, 93, 93), (2681472, 2681473, 94, 94),
        (2681474, 2681475, 95, 95), (2681476, 2681477, 96, 96), (2681478, 2681479, 97, 97),
        (63209, 63198, 161, 161), (63210, 63199, 162, 162), (63143, 63144, 107, 107), (63145, 63146, 108, 108),
        (2681358, 2681359, 87, 88)
    ];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        PromotionCatalogEntry Load(int id)
        {
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            return ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } })).Activities.Single();
        }
        foreach (var (egg, output, species, outputSpecies) in RealRelations)
        {
            var relation = resources.CreatureEggs.Get(egg);
            That(relation is not null && relation.EggItemId == egg && relation.OutputItemId == output && relation.Species == species &&
                resources.Creatures.Get(output) is { Egg: false } adult && adult.Species == outputSpecies,
                "真实蛋保持精确单向output关系及对应成年消费者（允许跨species） " + egg);
            var entry = Load(egg);
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实宠物蛋孵化消费者 " + egg + ": " + entry.Error);
        }
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        Fixture New() => new(resources);
        That(New().Validate() is null, "隔离蛋经严格关系连接合法成年宠物");
        That(New().Validate(tierItem: Adult) is null, "原普通非蛋宠物消费者继续通过");
        Reject(New(), "缺目录注入拒绝，不只移除原Egg守卫", includeEggs: false);
        var missing = New();
        missing.Relations.Clear();
        Reject(missing, "源虽标为蛋但无typed孵化关系仍拒绝");
        var wrongSource = New();
        wrongSource.Relations[Source] = wrongSource.Relations[Source] with { EggItemId = Source + 10 };
        Reject(wrongSource, "typed关系异源身份拒绝");
        var wrongOutput = New();
        wrongOutput.Relations[Source] = wrongOutput.Relations[Source] with { OutputItemId = EquipmentLeaf };
        Reject(wrongOutput, "typed关系指向非Creature产物拒绝");
        var nonCreatureOutput = New();
        nonCreatureOutput.Relations[Source] = nonCreatureOutput.Relations[Source] with { OutputItemId = EquipmentLeaf };
        nonCreatureOutput.Creatures[Source] = nonCreatureOutput.Creatures[Source] with { OutputItemId = EquipmentLeaf };
        Reject(nonCreatureOutput, "源与关系一致时仍拒绝非Creature产物");
        var wrongSpecies = New();
        wrongSpecies.Relations[Source] = wrongSpecies.Relations[Source] with { Species = 21 };
        Reject(wrongSpecies, "typed关系保留源species，不能与源定义不符");
        var crossSpecies = New();
        crossSpecies.Creatures[Adult] = crossSpecies.Creatures[Adult] with { Species = 21 };
        crossSpecies.Relations[Source] = CreatureEggCatalog.Read(
            new($"[equipment type]\n`[creature]` 0\n[sub type]\n1\n[creature species]\n20\n[output index]\n{Adult}"),
            new("[equipment type]\n`[creature]` 0\n[sub type]\n0\n[creature species]\n21"),
            crossSpecies.SourceTemplate, crossSpecies.AdultTemplate)
            ?? throw new InvalidDataException("精确output允许源20至目标21的跨species关系");
        That(crossSpecies.Relations[Source].Species == 20 && crossSpecies.Validate() is null,
            "精确output跨species合法，typed仍保留源species，不用相等假设覆盖目标");
        var negativeSource = New();
        negativeSource.Creatures[Source] = negativeSource.Creatures[Source] with { Species = -1 };
        negativeSource.Relations[Source] = negativeSource.Relations[Source] with { Species = -1 };
        Reject(negativeSource, "源及typed负species仍拒绝");
        var forbidden = New();
        forbidden.Put(forbidden.SourceTemplate with { UseForbidden = true });
        Reject(forbidden, "真实孵化服务禁用源模板拒绝");
        foreach (var change in new Func<CreatureDefinition, CreatureDefinition>[]
        {
            adult => adult with { ItemId = Adult + 10 }, adult => adult with { Species = -1 },
            adult => adult with { Egg = true }, adult => adult with { MaximumLevel = 0 },
            adult => adult with { ItemsPerLevel = 0 }, adult => adult with { ArtifactSlots = 8 }
        })
        {
            var badAdult = New();
            badAdult.Creatures[Adult] = change(badAdult.Creatures[Adult]);
            Reject(badAdult, "孵化产物保留普通Creature定义守卫");
        }
        var badSlot = New();
        badSlot.Put(badSlot.AdultTemplate with { EquipmentSlot = 27 });
        Reject(badSlot, "成年宠物孔位错误拒绝");
        var badMode = New();
        badMode.Put(badMode.AdultTemplate with { CreatureInitialMode = 2 });
        Reject(badMode, "成年宠物初始模式错误拒绝");

        var transform = New();
        transform.Put(transform.AdultTemplate with { Expiration = new(DailyDelete: 2) });
        That(transform.Validate() is null,
            "孵化只转换原UID及期限，目标不套新实例发放Edge的创建期限守卫");
        That(transform.Validate(tierItem: Adult) is not null,
            "同目标作为直接新奖励时仍保留原创建期限拒绝");
    }

    static void Reject(Fixture fixture, string label, bool includeEggs = true)
    {
        string? error = fixture.Validate(includeEggs);
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
        readonly CreatureCatalog creatureCatalog;
        readonly CreatureEggCatalog eggs;
        public ItemTemplate SourceTemplate { get; }
        public ItemTemplate AdultTemplate { get; }
        public ConcurrentDictionary<int, CreatureEggDefinition> Relations { get; }
        public ConcurrentDictionary<int, CreatureDefinition> Creatures { get; }

        public Fixture(ItemResources resources)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            SourceTemplate = new(Source, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "隔离宠物蛋") { CreatureEgg = true, CreatureSubType = 1 };
            AdultTemplate = SourceTemplate with { Id = Adult, Name = "隔离成年宠物", CreatureEgg = false, CreatureSubType = 0 };
            Put(SourceTemplate);
            Put(AdultTemplate);
            Put(new(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 0,
                "[free]", ushort.MaxValue, 0, "合法装备控制叶"));
            creatureCatalog = new(_ => throw new InvalidDataException("隔离蛋不应读取equipment资源"),
                _ => throw new InvalidDataException("隔离蛋不应读取creature资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>(), new Dictionary<int, string>(), null);
            Creatures = Field<ConcurrentDictionary<int, CreatureDefinition>>(creatureCatalog, "creatures");
            Creatures[Source] = new(Source, 20, true, 20, 0, false, 0, Adult, 0, 0, 20) { ArtifactSlots = 7 };
            Creatures[Adult] = new(Adult, 20, false, 20, 0, false, 0, 0, 0, 0, 20) { ArtifactSlots = 7 };
            eggs = new(archive, new Dictionary<int, string>(), items);
            Relations = Field<ConcurrentDictionary<int, CreatureEggDefinition>>(eggs, "definitions");
            Relations[Source] = CreatureEggCatalog.Read(
                new($"[equipment type]\n`[creature]` 0\n[sub type]\n1\n[creature species]\n20\n[output index]\n{Adult}"),
                new("[equipment type]\n`[creature]` 0\n[sub type]\n0\n[creature species]\n20"),
                SourceTemplate, AdultTemplate) ?? throw new InvalidDataException("隔离蛋严格关系构造失败");
            boosters = new(archive, new Dictionary<int, string>());
            avatars = new(_ => throw new InvalidDataException("隔离蛋不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {EquipmentLeaf} 1 0 0 100 0 0 0\n");
            rules = new(new("creature-egg-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, EquipmentLeaf, 1)], [new(2, Source, 1)]);
        }

        public void Put(ItemTemplate template) => items.PrefillTemplates([KeyValuePair.Create(template.Id, template)]);

        public string? Validate(bool includeEggs = true, int? tierItem = null) => new PromotionConsumerValidation(items, boosters,
            avatars, shop, resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
            creatureCatalog, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments, resources.Enhancements,
            resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades,
            creatureEggs: includeEggs ? eggs : null).Validate(tierItem is { } id
                ? rules with { Tiers = [new(2, id, 1)] } : rules);
    }

    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
