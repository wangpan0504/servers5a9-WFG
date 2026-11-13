using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CarbonCrystalDropTests
{
    public static void RunCore(DungeonDropRuleCatalog rules)
    {
        int before = Count;
        ItemTemplate Item(int id) => new(id, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成碳结晶体");
        // 合成概率只用来覆盖命中边界，不代表本服默认值。
        var policy = new CarbonCrystalDropPolicy(Item, 200, 2, 4);
        var map = new DungeonMap("boss", new(0, 0), 1, "map/合成碳结晶体.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 1, 0, byte.MaxValue, 0, maze, map, map) { BasisLevel = 55 };
        var source = new DungeonRoomMonster(42, [], "[fixed]", "[boss]", "monster/合成领主.mob");
        var boss = new DungeonPlannedMonster(0, 10, 42, 1, source) { SpawnType = 3, SourceMap = map };
        var original = new DungeonDropGenerationPlan("原规则", []) { OrdinaryLukeDailySource = 42 };
        var defaults = new CarbonCrystalDropPolicy(Item).Extend(original, entry, boss);
        var configured = defaults.Trials.Single();
        That(configured.Threshold == 333 && configured.ProbabilityDenominator == 1000 &&
            configured.MinimumCount == 1 && configured.MaximumCount == 1 && configured.Attempts == 1 &&
            !configured.GrowthContractEligible && configured.Category == DungeonDropCategory.Materials,
            "正式本服兼容规则独立333/1000掉1个，不叠加成长契约或尝试次数");
        for (int roll = 0; roll < 1000; roll++)
        {
            var generated = defaults.Generate(bound => bound == 1000 ? roll : 0, DateTimeOffset.UnixEpoch);
            That(roll < 333 ? generated is [{ Item.ItemId: CarbonCrystalDropPolicy.ItemId, Item.Count: 1 }] : generated.IsEmpty,
                "默认概率完整枚举恰有333个命中点，未命中不发材料或保底");
        }
        var extended = policy.Extend(original, entry, boss);
        That(extended.MaximumObjects == 1 && extended.OrdinaryLukeDailySource == 42 &&
            extended.ResourceSource.Contains(CarbonCrystalDropPolicy.Rule), "碳材料沿原计划追加一堆并保留既有来源资格");
        foreach (int roll in new[] { 0, 2 })
        {
            var generated = extended.Generate(bound => bound == 1000 ? 199 : bound == 3 ? roll : 0, DateTimeOffset.UnixEpoch);
            That(generated is [{ Item.ItemId: CarbonCrystalDropPolicy.ItemId }] && generated[0].Item!.Count == 2 + roll,
                "碳材料概率末个命中点及数量两端生成可拾取地面堆");
        }
        That(extended.Generate(bound => bound == 1000 ? 200 : 0, DateTimeOffset.UnixEpoch).IsEmpty,
            "概率上界不命中，不能变为保底");
        That(ReferenceEquals(new CarbonCrystalDropPolicy(Item, 0, 1, 1).Extend(original, entry, boss), original),
            "显式零概率不追加掉落");
        foreach (var badEntry in new[] { entry with { BasisLevel = 54 }, entry with { BasisLevel = null },
            entry with { DisableFatigueConsumption = true }, entry with { IsPractice = true },
            entry with { IsTutorial = true }, entry with { DisableItemDrops = true },
            entry with { IsTayberrs = true }, entry with { IsTayberrsStory = true }, entry with { QuestId = 1 } })
            That(ReferenceEquals(policy.Extend(original, badEntry, boss), original),
                "低等级、未知等级、无疲劳或专属接管场景不追加碳材料");
        foreach (var badBoss in new[] { boss with { SourceMap = null }, boss with { Source = null },
            boss with { Source = source with { RankKind = null } }, boss with { IsFriendly = true },
            boss with { IsBlocking = false }, boss with { IsDummyMapBoss = true }, boss with { SpawnType = 5 },
            boss with { Source = source with { Modifiers = ["[dummy]"] } },
            boss with { Source = source with { ScriptedRetirement = true } }, boss with { ObjectKey = entry.CharacterId },
            boss with { HellWave = new(2, 216, 42, 95, 3, 10, 0) },
            boss with { AiCharacter = new(1, [], "[monster]", "[boss]", "apc/合成.apc", 95) } })
            That(ReferenceEquals(policy.Extend(original, entry, badBoss), original), "非真实敌对领主不追加碳材料");
        That(CarbonCrystalDropPolicy.CanReward(entry with { QuestId = 1, IsOrdinaryHunt = true }, boss) &&
            CarbonCrystalDropPolicy.CanReward(entry with { QuestId = 1, RouteQuestId = 1 }, boss) &&
            CarbonCrystalDropPolicy.CanReward(entry with { BasisLevel = 95 }, boss with { CandidateLevel = 54 }),
            "普通猎杀及真实任务疲劳路线保留来源，等级门槛取副本而非怪物或角色等级");
        foreach (var (rate, minimum, maximum) in new[] { (-1, 1, 1), (1001, 1, 1), (1, 0, 1), (1, 2, 1), (1, 1, 501) })
            Reject<InvalidDataException>(() => new CarbonCrystalDropPolicy(Item, rate, minimum, maximum), "非法碳材料配置拒绝");
        Reject<InvalidDataException>(() => new CarbonCrystalDropPolicy(id => Item(id) with { Kind = ItemKind.Quest }, 1000, 1, 1),
            "错误材料模板拒绝");

        var settings = new DungeonDropControlSettings();
        settings.Replace([new(DungeonDropScope.Ordinary, [], new(Materials: 0))], true);
        var resourcePolicy = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([]),
            id => new(id, source.Path, null, null), Item, dropControl: settings, carbonCrystal: policy);
        var prepare = resourcePolicy.PrepareEntry(entry);
        That(prepare(boss).Trials[^1].Threshold == 0, "碳材料在统一材料倍率之前追加，可被零倍率关闭");
        var cached = prepare(boss);
        var specialChild = boss with { Source = source with { SpecialChild = new(0, "test", [], null) } };
        That(prepare(specialChild).Trials.Length + 1 == cached.Trials.Length &&
            ReferenceEquals(cached, prepare(boss with { ObjectKey = 11 })),
            "同模板特殊演员不得借用领主缓存，合法领主仍复用不可变计划");
        Console.WriteLine($"PASS {Count - before} 碳结晶体领主获取、概率、排除与缓存断言");
    }

    public static void RunResources(ItemCatalog items)
    {
        var item = items.Get(CarbonCrystalDropPolicy.ItemId);
        _ = new CarbonCrystalDropPolicy(items.Get);
        That(item.Kind == ItemKind.Material && item.Name == "碳结晶体" && item.CreationRate == 0,
            "匹配PVF的碳结晶体为可创建材料，不借creation rate伪造领主掉率");
    }
}
