using System.Collections;
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

// 副职业用途按当前物品的真实制作成本核对，不向奖励图登记其它成本。
internal static class PromotionExpertJobConsumerTests
{
    const int Equipment = 880014001, Root = 880014002, Child = 880014003, Unknown = 880014004;

    public static void RunResources(ItemResources resources)
    {
        var scripts = resources.ExpertJobs;
        That(scripts.TryRecipe(1, 2600141, out var ordinary) && ordinary.ProductId == 2610034 &&
            ordinary.Materials.SequenceEqual(new[] { (3033, 10), (2610030, 1) }),
            "真实2610030成本owner是2600141制作，不取商店need material");
        That(scripts.TryCardRecipe(3619, out var card) && card.CardId == 3619 && card.RecipeId == 10015129 &&
            card.BeadId == 2600313 && card.Materials.Contains((3227, 5)) && card.Materials.Contains((3166, 20)) &&
            card.BeadMaterials.SequenceEqual(new[] { (2610039, 3) }) &&
            resources.Enchantments.Get(card.BeadId)?.CardItemId == card.CardId,
            "真实3227成本与3619卡片owner保留珠宝映射及全部实际成本");
        foreach (int id in new[] { 2610030, 3227, 3619 })
            That(resources.Items.Get(id).Kind == ItemKind.Profession, "真实正例是Profession源 " + id);
        That(resources.Items.Get(3166).Kind == ItemKind.Material,
            "深层3166反例保持普通Material种类，避免以错误fixture证明同根边界");

        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        foreach (int id in new[] { 2610030, 3227, 3619 })
        {
            int sequence = original.Rules.Tiers[0].Sequence;
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence
                    ? tier with { ItemId = id, Count = 1 } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "最小RED真实Profession源须连接精确副职业消费者 " + id + ": " + entry.Error);
        }

        var fixture = new Fixture(resources);
        That(fixture.Validate(2610030) is null && fixture.Validate(3227) is null && fixture.Validate(3619) is null,
            "隔离图注入相同ExpertJobs目录后精确三叶有消费者");
        string? unknown = fixture.Validate(Unknown);
        That(unknown is not null && unknown.Contains(Unknown + ":", StringComparison.Ordinal),
            "同material expert job标签但无typed owner的Profession继续拒绝: " + unknown);
        string? deep = fixture.Validate(2610030, 3166);
        That(deep is not null && deep.Contains("3166: material_consumer_not_connected", StringComparison.Ordinal) &&
            !deep.Contains("2610030:", StringComparison.Ordinal),
            "另一真实副职业配方的3166成本不得通过当前2610030 owner查询污染materialInputs: " + deep);
        string? alias = fixture.Validate(2610030, alias: true);
        That(alias is not null && alias.Contains("2610030", StringComparison.Ordinal),
            "具体成本ID不能借用别名模板: " + alias);

        // 只保留上述真实owner，避免其它合法配方掩盖本候选缺失的完整合同。
        var ownerBaseline = new Fixture(resources, isolateOwners: true);
        That(ownerBaseline.Validate(2610030) is null && ownerBaseline.Validate(3619) is null,
            "完整真实制作和制珠owner在隔离候选目录中仍须通过，反例不能借坏夹具全拒绝");
        string? missingProduct = new Fixture(resources, isolateOwners: true).WithoutTemplate(2610034).Validate(2610030);
        That(missingProduct is not null && missingProduct.Contains("2610030: expert_job_consumer_definition", StringComparison.Ordinal),
            "2610030匹配owner缺产物须拒绝: " + missingProduct);
        That(new Fixture(resources, isolateOwners: true).WithoutTemplate(3033).Validate(2610030) is null,
            "3033是黑色晶体钱包成本，真实Spend不依赖实体模板");
        string? walletCost = new Fixture(resources, isolateOwners: true)
            .WithTemplate(3033, resources.Items.Get(3033) with { StackLimit = 0 }).Validate(2610030);
        That(walletCost is null, "钱包成本不套实体堆叠定义资格: " + walletCost);
        string? wrongProduct = new Fixture(resources, isolateOwners: true)
            .WithTemplate(2610034, resources.Items.Get(2610034) with { Id = 2610035 }).Validate(2610030);
        That(wrongProduct is not null && wrongProduct.Contains("2610030: expert_job_consumer_definition", StringComparison.Ordinal),
            "产物模板别名不能证明2610030有实际制作owner: " + wrongProduct);

        var bead = resources.Enchantments.Get(2600313)!;
        foreach (var broken in new[]
        {
            new Fixture(resources, isolateOwners: true).WithBead(null),
            new Fixture(resources, isolateOwners: true).WithBead(bead with { CardItemId = 3620 }),
            new Fixture(resources, isolateOwners: true).WithoutTemplate(2610039)
        })
        {
            string? error = broken.Validate(3619);
            That(error is not null && error.Contains("3619: expert_job_consumer_definition", StringComparison.Ordinal),
                "3619缺宝珠/卡映射不一致/缺独立BeadMaterials均不能只凭KnownCards放行: " + error);
        }

        // 坏匹配owner排序在真实2600141前；其资源异常不能污染合法替代owner。
        That(new Fixture(resources, isolateOwners: true).WithBrokenCandidate(matches: true).Validate(2610030) is null,
            "坏匹配owner后仍有真实2600141时，任一完整合法owner足以证明用途");
        That(new Fixture(resources, isolateOwners: true).WithBrokenCandidate(matches: false).Validate(2610030) is null,
            "不匹配当前源的坏配方不能污染真实2600141 owner");
    }

    sealed class Fixture
    {
        readonly ItemResources resources;
        readonly ItemCatalog items;
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly ConcurrentDictionary<int, BoosterDefinition> definitions;
        readonly ExpertJobScriptCatalog scripts;
        readonly EnchantmentCatalog enchantments;
        readonly ConcurrentDictionary<int, Lazy<EnchantmentDefinition?>> beadDefinitions;

        public Fixture(ItemResources resources, bool isolateOwners = false)
        {
            this.resources = resources;
            scripts = isolateOwners ? IsolatedOwners(resources.ExpertJobs) : resources.ExpertJobs;
            enchantments = new(_ => throw new InvalidDataException("隔离fixture不读取额外附魔脚本"),
                new Dictionary<int, string>());
            beadDefinitions = (ConcurrentDictionary<int, Lazy<EnchantmentDefinition?>>)typeof(EnchantmentCatalog)
                .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(enchantments)!;
            beadDefinitions[2600313] = new(() => resources.Enchantments.Get(2600313));
            var archive = (PvfArchive)typeof(ItemCatalog).GetField("pvf", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(resources.Items)!;
            items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            var equipment = new ItemTemplate(Equipment, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            var box = equipment with { Id = Root, Kind = ItemKind.Consumable, EquipmentSlot = 0, StackableType = "[booster] 0" };
            var unknown = resources.Items.Get(2610030) with { Id = Unknown };
            items.PrefillTemplates(new[] { equipment, box, box with { Id = Child }, unknown }
                .Concat(new[] { 3033, 2610030, 3227, 3619, 3166, 2610034, 2610039, 2600313, 2600141, 10015129 }.Select(resources.Items.Get))
                .Select(item => KeyValuePair.Create(item.Id, item)));
            boosters = new(archive, new Dictionary<int, string>());
            definitions = (ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
                .GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(boosters)!;
            avatars = new(_ => throw new InvalidDataException("隔离副职业叶不读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {Equipment} 1 0 0 100 0 0 0\n");
        }

        public Fixture WithoutTemplate(int id)
        {
            ((ConcurrentDictionary<int, ItemTemplate>)typeof(ItemCatalog)
                .GetField("templates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(items)!).TryRemove(id, out _);
            return this;
        }

        public Fixture WithTemplate(int id, ItemTemplate template)
        {
            items.PrefillTemplates([KeyValuePair.Create(id, template)]);
            return this;
        }

        public Fixture WithBead(EnchantmentDefinition? bead)
        {
            beadDefinitions[2600313] = new(() => bead);
            return this;
        }

        public Fixture WithBrokenCandidate(bool matches)
        {
            const int recipeId = 2600140;
            scripts.TryRecipe(1, 2600141, out var ordinary);
            scripts.TryLearningRecipe(2600141, out var learning);
            var jobs = (IDictionary)typeof(ExpertJobScriptCatalog)
                .GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scripts)!;
            var job = jobs[(byte)1]!;
            var recipes = (Dictionary<int, ExpertJobRecipe>)job.GetType().GetProperty("Recipes")!.GetValue(job)!;
            recipes[recipeId] = ordinary with
            {
                RecipeId = recipeId, ProductId = 880014006,
                Materials = matches ? ordinary.Materials : [(Unknown, 1)]
            };
            ((Dictionary<int, ExpertJobLearnableRecipe>)typeof(ExpertJobScriptCatalog)
                .GetField("learnable", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scripts)!)
                [recipeId] = learning with { ItemId = recipeId };
            return WithTemplate(recipeId, resources.Items.Get(2600141) with { Id = recipeId });
        }

        static ExpertJobScriptCatalog IsolatedOwners(ExpertJobScriptCatalog source)
        {
            // 仅测试副本：复制job对象再替换它们的Recipes，不改共享EXJ目录或任何原字典。
            var jobs = (IDictionary)typeof(ExpertJobScriptCatalog)
                .GetField("jobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
            var isolatedJobs = (IDictionary)Activator.CreateInstance(jobs.GetType())!;
            source.TryRecipe(1, 2600141, out var ordinary);
            var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (DictionaryEntry entry in jobs)
            {
                var job = clone.Invoke(entry.Value, null)!;
                job.GetType().GetProperty("Recipes")!.SetValue(job, (byte)entry.Key == 1
                    ? new Dictionary<int, ExpertJobRecipe> { [ordinary.RecipeId] = ordinary }
                    : new Dictionary<int, ExpertJobRecipe>());
                isolatedJobs[entry.Key] = job;
            }
            var result = (ExpertJobScriptCatalog)typeof(ExpertJobScriptCatalog)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([isolatedJobs]);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var learning = (Dictionary<int, ExpertJobLearnableRecipe>)typeof(ExpertJobScriptCatalog)
                .GetField("learnable", flags)!.GetValue(result)!;
            foreach (var row in (Dictionary<int, ExpertJobLearnableRecipe>)typeof(ExpertJobScriptCatalog)
                .GetField("learnable", flags)!.GetValue(source)!) learning.Add(row.Key, row.Value);
            source.TryCardRecipe(3619, out var card);
            ((Dictionary<int, ExpertJobCardRecipe>)typeof(ExpertJobScriptCatalog)
                .GetField("cardRecipes", flags)!.GetValue(result)!).Add(card.CardId, card);
            return result;
        }

        public string? Validate(int profession, int? deepMaterial = null, bool alias = false)
        {
            items.PrefillTemplates([KeyValuePair.Create(2610030,
                resources.Items.Get(2610030) with { Id = alias ? 2610031 : 2610030 })]);
            definitions[Root] = new([new(1, [new(Equipment, 1, 1), new(Child, 1, 1)])]);
            definitions[Child] = new([new(1, deepMaterial is { } material
                ? [new(profession, 1, 1), new(material, 1, 1)] : [new(profession, 1, 1)])]);
            var rules = new PromotionRules(new("expert-job-consumer-fixture", "period", "v1", "default", "package"),
                10, 3, 31, Equipment, 1, 5000, [new(1, Equipment, 1)], [new(2, Root, 1)]);
            return Validator().Validate(rules);
        }

        PromotionConsumerValidation Validator() => new(items, boosters, avatars, shop, resources.EpicPieces, resources.Contracts,
                resources.RandomOptions, resources.AttributePairs, resources.Creatures, resources.CollectBoxes,
                resources.LimitCubes, enchantments, resources.Enhancements, resources.RecoveryConsumables,
                resources.Amplifications, resources.AmplifyUpgrades, expertJobs: () => scripts);
    }
}
