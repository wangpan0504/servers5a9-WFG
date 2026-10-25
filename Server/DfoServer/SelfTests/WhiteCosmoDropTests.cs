using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class WhiteCosmoDropTests
{
    public static void RunCore(DungeonEntryPlan basis, DungeonHellRewardPolicy hell, DungeonItemDropPool normal,
        DungeonItemDropPool tayberrs, DungeonItemDropPool cosmoPool, DungeonItemDropPool superPool)
    {
        var map = basis.BossMap with { Id = 92697, Path = "map/合成白斯.map" };
        var source = new DungeonRoomMonster(HarlemHellEpicDropPolicy.WhiteBossId, [109007097, 1], "[fixed]", "[boss]", "monster/合成白斯.mob");
        var catalog = new DungeonCosmoRewardCatalog(2, 4, [new(map, [source], [], [])]);
        var policy = new HarlemHellEpicDropPolicy([1], normal, tayberrs, tayberrs, cosmoPool, cosmo: catalog, superPool: superPool);
        var entry = basis with { DungeonId = 7191, Difficulty = 4, BasisLevel = 1, HellParty = null, BossMap = map };
        var boss = new DungeonPlannedMonster(0, 10, source.MonsterId, 1, source) { SpawnType = 3 };
        var empty = new DungeonDropGenerationPlan("白斯边界", []);
        var trial = policy.Extend(empty, entry, boss, hell).Trials.Last();
        That(trial.Threshold == 200 && trial.Attempts == 2 && trial.ProbabilityDenominator == 1001 &&
            trial.RarityPools[4].AlternatePermille == 1000 && trial.RarityPools[4].SecondaryPermille == 0 &&
            trial.RarityPools[4].SecondaryItems is null, "白斯仅改变史诗条件池，总装备概率、稀有度及骰数不提升");
        var isolated = new DungeonDropGenerationPlan("白斯史诗", [trial]);
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (int branch = 0; branch < 1000; branch++)
        {
            var drops = isolated.Generate(bound => bound == 1000 ? branch : 0, now);
            That(drops.Length == 2 && drops.All(drop => tayberrs.Entries.Any(item => item.Template.Id == drop.Item!.ItemId)),
                "白斯全部千分比边界只从泰波尔斯取物品，不返回哈林或超界");
        }
        That(isolated.Generate(bound => bound == 1001 ? 200 : 0, now).IsEmpty, "白斯装备骰未命中不补发史诗");
        var baseline = hell.Extend(empty, 1, 1, 2, 4, suppressEpicFragments: true, epicEquipment: cosmoPool).Trials.Last();
        That(trial.RarityPools.Select(row => row.Threshold).SequenceEqual(baseline.RarityPools.Select(row => row.Threshold)) &&
            trial.RarityPools.Where((_, index) => index != 4).SelectMany(row => row.Items.Entries)
                .SequenceEqual(baseline.RarityPools.Where((_, index) => index != 4).SelectMany(row => row.Items.Entries)),
            "白斯保留全部稀有度阈值及其它稀有度候选，不把未命中史诗的结果升级");
        var expired = new DungeonItemDropPool(tayberrs.Entries.Select(row => new DungeonWeightedDropItem(
            row.Template with { Expiration = new(now.ToUnixTimeSeconds() - 1) }, 1)));
        var expiredPolicy = new HarlemHellEpicDropPolicy([1], normal, tayberrs, expired, cosmoPool, cosmo: catalog, superPool: superPool);
        var expiredTrial = expiredPolicy.Extend(empty, entry, boss, hell).Trials.Last();
        int draws = 0;
        That(new DungeonDropGenerationPlan("过期白斯", [expiredTrial]).Generate(_ => { draws++; return 0; }, now).IsEmpty && draws == 8,
            "白斯命中过期泰波尔斯候选冻结为空，不重抽哈林或超界");
        Reject<InvalidDataException>(() => new HarlemHellEpicDropPolicy([1], normal, tayberrs, null, cosmoPool,
            new() { CosmoTayberrsPermille = 0 }, catalog, superPool), "白斯不可因其它领主泰波尔斯配置为0而静默缺池");

        VerifyOriginalTrials(policy, entry, boss, hell, cosmoPool, tayberrs, now);

        ItemTemplate Material(int id) => new(id, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1, "[trade]", ushort.MaxValue, 0, "合成材料");
        var materialPolicy = new HarlemMaterialDropPolicy(new(), [1], Material, policy);
        var plan = materialPolicy.Extend(empty, entry, boss);
        var contents = plan.Generate(_ => 0, now);
        That(contents.Length == 42 && plan.MaximumObjects == 42 && contents.All(row => row.Item!.Count == 1) &&
            contents.Count(row => row.Item!.ItemId == HarlemMaterialDropPolicy.AntimatterId) == 2,
            "白斯独立增加两颗普通反物质，保留30碎片和10天空之愿，完整对象容量计入42");
        foreach (var invalid in new[] { entry with { DungeonId = 530 }, entry with { IsPractice = true },
            entry with { Difficulty = 0 }, entry with { RouteQuestId = 1 }, entry with { BossMap = map with { Path = "map/其它.map" } } })
            That(!policy.IsWhiteBoss(invalid, boss) && ReferenceEquals(materialPolicy.Extend(empty, invalid, boss), empty),
                "错误副本、练习、难度、任务路线或伪造终点不借白斯发奖");
        foreach (var invalid in new[] { boss with { IsFriendly = true }, boss with { IsDummyMapBoss = true },
            boss with { Source = source with { RawValues = [0] } }, boss with { MonsterId = 42 }, boss with { HellWave = new(2, 216, source.MonsterId, 1, 0, 10, 0) } })
            That(!policy.IsWhiteBoss(entry, invalid) && ReferenceEquals(materialPolicy.Extend(empty, entry, invalid), empty),
                "友方、假首领、伪造来源、其它怪物和深渊波次不借白斯发奖");
        var disabled = new HarlemMaterialDropPolicy(new() { Enabled = false }, [1], Material, policy);
        That(ReferenceEquals(disabled.Extend(empty, entry, boss), empty), "关闭本服材料规则同时关闭白斯追加，不另造奖励入口");
        var multiplied = new HarlemMaterialDropPolicy(new() { CosmoMaterialMultiplier = 3 }, [1], Material, policy)
            .Extend(empty, entry, boss).Generate(_ => 0, now);
        That(multiplied.Count(row => row.Item!.ItemId == HarlemMaterialDropPolicy.AntimatterId) == 2,
            "白斯固定2粒子不随通用材料倍数或装备骰数放大");
    }

    static void VerifyOriginalTrials(HarlemHellEpicDropPolicy policy, DungeonEntryPlan entry, DungeonPlannedMonster boss,
        DungeonHellRewardPolicy hell, DungeonItemDropPool sourcePool, DungeonItemDropPool targetPool, DateTimeOffset now)
    {
        var epic = sourcePool.Entries[0].Template;
        var material = new ItemTemplate(9001, ItemKind.Material, 0, 1, 0, 99, 1, 0, 1, "[trade]", ushort.MaxValue, 0, "混合材料");
        var rare = epic with { Id = 9002, Rarity = 3 };
        var mixed = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(epic, 1), new(material, 1), new(rare, 1) });
        var counts = ImmutableDictionary<int, int>.Empty.Add(epic.Id, 2).Add(material.Id, 7).Add(rare.Id, 1);
        var direct = new DungeonDropTrial(1000, mixed, 1, 1, ProbabilityDenominator: 1000, ItemCounts: counts);
        var generic = hell.Extend(new("原装备骰", []), 1, 1, 1, 4, true, sourcePool).Trials.Single();
        var original = new DungeonDropGenerationPlan("原普通装备及混合直接掉落", [generic, direct]);
        var modified = policy.Extend(original, entry, boss, hell);
        That(modified.Trials.Length == 3 && modified.Trials.Take(2).All(row => ReferenceEquals(row.EpicReplacementItems, targetPool)) &&
            modified.Trials[^1].EpicReplacementItems is null && modified.Trials[0].Threshold == generic.Threshold &&
            modified.Trials[0].RarityPools.SequenceEqual(generic.RarityPools),
            "仅白斯原试验添加史诗替换，原装备阈值稀有度完整保留且追加试验不二次替换");
        foreach (var trial in modified.Trials.Take(2))
        {
            var result = new DungeonDropGenerationPlan("原试验史诗命中", [trial]).Generate(_ => 0, now);
            That(result.Length == (trial.Items is null ? 1 : 2) && result.All(row =>
                targetPool.Entries.Any(candidate => candidate.Template.Id == row.Item!.ItemId)),
                "通用稀有度史诗骰与混合直掉史诗均替换为泰波尔斯，按原ID取数量不丢2件");
        }
        var mixedTrial = modified.Trials[1];
        for (int selected = 1; selected <= 2; selected++)
        {
            var result = new DungeonDropGenerationPlan("混合非史诗分支", [mixedTrial]).Generate(bound => bound == 3 ? selected : 0, now);
            That(result.Length == 1 && result[0].Item!.ItemId == (selected == 1 ? material.Id : rare.Id) &&
                result[0].Item!.Count == (selected == 1 ? 7 : 1), "混合池材料与非史诗装备不替换，完整原数量保留");
        }
        That(new DungeonDropGenerationPlan("混合未命中", [mixedTrial with { Threshold = 0 }]).Generate(_ => 0, now).IsEmpty,
            "原直掉未触发不能补白斯史诗");
        var emptyPool = new DungeonItemDropPool(Array.Empty<ItemTemplate>());
        That(new DungeonDropGenerationPlan("原空池", [new DungeonDropTrial(1000, emptyPool, 1, 1,
            ProbabilityDenominator: 1000, EpicReplacementItems: targetPool)]).Generate(_ => 0, now).IsEmpty,
            "原空池不会因专属候选池存在而变成奖励");
        var expiredSource = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(epic with { Expiration = new(now.ToUnixTimeSeconds() - 1) }, 1) });
        int calls = 0;
        var expiredTrial = new DungeonDropTrial(1000, expiredSource, 1, 1, ProbabilityDenominator: 1000, EpicReplacementItems: targetPool);
        That(new DungeonDropGenerationPlan("原过期空抽", [expiredTrial]).Generate(_ => { calls++; return 0; }, now).IsEmpty && calls == 2,
            "原候选已过期则在替换前冻结为空，不抽泰波尔斯续期");
        var expiredTarget = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(targetPool.Entries[0].Template with
            { Expiration = new(now.ToUnixTimeSeconds() - 1) }, 1) });
        calls = 0;
        That(new DungeonDropGenerationPlan("目标过期空抽", [expiredTrial with { Items = sourcePool, EpicReplacementItems = expiredTarget }])
            .Generate(_ => { calls++; return 0; }, now).IsEmpty && calls == 3,
            "目标候选过期只空抽一次，不重抽或退回原史诗");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("无效替换池", [direct with { EpicReplacementItems = emptyPool }]),
            "专属空候选池明确拒绝");
        var wrong = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(material, 1) });
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("材料替换装备", [direct with { EpicReplacementItems = wrong }]),
            "专属替换池不能包含非史诗装备");
        var unsupported = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(epic with { Expiration = new(DailyDelete: 2) }, 1) });
        Reject<NotSupportedException>(() => new DungeonDropGenerationPlan("目标期限未知", [direct with { EpicReplacementItems = unsupported }]),
            "专属候选未知期限同样在准备阶段拒绝");
        That(new DungeonDropGenerationPlan("替换容量", [mixedTrial]).MaximumObjects == 2,
            "史诗替换容量按原分支完整物品数量计算，不因目标ID没有ItemCounts而漏算");
        var outside = policy.Extend(original, entry with { DungeonId = 530 }, boss, hell);
        That(ReferenceEquals(outside, original) && outside.Trials.All(row => row.EpicReplacementItems is null),
            "普通图相同白斯模板不启用替换管线");
    }

    public static void RunResources(ItemResources items, HarlemHellEpicDropPolicy policy, DungeonEntryPlan entry, DungeonPlannedMonster boss)
    {
        var ordinary = items.Items.Get(HarlemMaterialDropPolicy.AntimatterId);
        var account = items.Items.Get(10309041);
        That(ordinary is { Name: "反物质粒子", Kind: ItemKind.Material } &&
            ordinary.AttachType.Trim('`', ' ', '\r', '\n') == "[trade]" &&
            ordinary.Expiration == new ItemExpiration() && account.Name == "特殊反物质粒子" &&
            account.AttachType.Trim('`', ' ', '\r', '\n') == "[account]",
            "真实PVF普通反物质与特殊账号绑定物品身份分离，不推断账号绑定替代概率");
        var materials = new HarlemMaterialDropPolicy(new(), [], items.Items.Get, policy)
            .Extend(new("真实白斯材料", []), entry, boss).Generate(_ => 0, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        bool white = boss.MonsterId == HarlemHellEpicDropPolicy.WhiteBossId;
        That(materials.Count(row => row.Item!.ItemId == HarlemMaterialDropPolicy.AntimatterId) == (white ? 2 : 0) &&
            materials.Count(row => row.Item!.ItemId == HarlemMaterialDropPolicy.FragmentId) == 30 &&
            materials.Count(row => row.Item!.ItemId == HarlemMaterialDropPolicy.SkyWishId) == 10 &&
            materials.All(row => row.Item!.Count == 1 && row.Item.ItemId != account.Id),
            "真实三个终点仅白斯追加2普通粒子，原有材料不丢失也不改账号绑定版");
    }
}
