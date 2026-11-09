using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class HarlemMaterialDropTests
{
    public static void RunCore()
    {
        int before = Count;
        var rules = HarlemMaterialDropRules.Parse("{}");
        That(rules.NormalFragments.SequenceEqual([9, 10, 12, 13, 15]) && rules.LegendFragments == 15 &&
            rules.SkyWishes.SequenceEqual([4, 6, 8, 8, 10]) && rules.CosmoMaterialMultiplier == 2 && rules.CosmoSkyWishMultiplier == 3 &&
            rules.DawnFragments == 200 && rules.DawnPurgedCrystallines == 120 && rules.DawnPurgedPermille == 50,
            "默认采用已授权本服规则，不冒充PVF概率或把9至15改成均匀随机");
        Reject<InvalidDataException>(() => HarlemMaterialDropRules.Parse("null"), "空规则不能静默启用默认奖励");
        Reject<InvalidDataException>(() => HarlemMaterialDropRules.Parse("{\"NormalFragments\":[9,15]}"), "五档数量缺失拒绝");
        Reject<InvalidDataException>(() => HarlemMaterialDropRules.Parse("{\"DawnPurgedPermille\":1001}"), "非法概率不截断");
        Reject<InvalidDataException>(() => HarlemMaterialDropRules.Parse("{\"DawnFragments\":0}"), "零数量不制造无效地面物品");
        Reject<JsonException>(() => HarlemMaterialDropRules.Parse("{\"DawnPurgedPercent\":5}"), "拼错配置名拒绝，不吞掉配置");

        ItemTemplate Item(int id) => new(id, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成哈林材料");
        var policy = new HarlemMaterialDropPolicy(rules, [291100308], Item);
        var empty = new DungeonDropGenerationPlan("合成无其它奖励", []);
        var map = new DungeonMap("boss", new(0, 0), 41207, "map/合成材料.map");
        var maze = new DungeonMaze(0, 1, 1, "AA", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 291100308, 0, byte.MaxValue, 0, maze, map, map)
            { HellParty = new(100, map.Position) };
        var wave = new DungeonPlannedMonster(0, 10, 107010514, 95, null)
            { HellWave = new(2, 216, 107010514, 95, 0, 10, 0), HellRewardRolls = 8 };
        HarlemAbyssBonusDropTests.RunCore(entry, wave);
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        for (byte difficulty = 0; difficulty < 5; difficulty++)
        {
            var normalPlan = policy.Extend(empty, entry with { Difficulty = difficulty }, wave);
            var legendPlan = policy.Extend(empty, entry with { Difficulty = difficulty, IsHellLegend = true }, wave);
            var normal = normalPlan.Generate(_ => 0, now);
            var legend = legendPlan.Generate(_ => 0, now);
            That(normal.Length == rules.NormalFragments[difficulty] && normalPlan.MaximumObjects == normal.Length &&
                normal.All(row => row.Item is { ItemId: HarlemMaterialDropPolicy.FragmentId, Count: 1 }) &&
                legend.Where(row => row.Item!.ItemId == HarlemMaterialDropPolicy.FragmentId).ToArray() is { Length: 15 } legendFragments &&
                legendFragments.All(row => row.Item!.Count == 1) &&
                legend.Where(row => row.Item!.ItemId == HarlemMaterialDropPolicy.SkyWishId).ToArray() is [{ Item.Count: int wishCount }] &&
                wishCount == rules.SkyWishes[difficulty] && legendPlan.MaximumObjects == 16,
                "普通与星空异形碎片逐个生成地面物品，天空之愿按单堆生成，五档总数量及完整对象容量不变；材料不乘装备骰数");
        }
        var oversized = new HarlemMaterialDropPolicy(rules with { LegendFragments = 500 }, [291100308], Item);
        Reject<InvalidDataException>(() => oversized.Extend(empty, entry with { IsHellLegend = true }, wave),
            "逐个材料超过完整地面容量时在准备阶段拒绝，不截断数量或重新合堆");
        That(ReferenceEquals(policy.Extend(empty, entry, wave with { HellRewardRolls = 0 }), empty) &&
            ReferenceEquals(policy.Extend(empty, entry, wave with { HellWave = null }), empty) &&
            ReferenceEquals(policy.Extend(empty, entry, wave with { IsFriendly = true }), empty) &&
            ReferenceEquals(policy.Extend(empty, entry with { DungeonId = 3 }, wave), empty) &&
            ReferenceEquals(policy.Extend(empty, entry with { HellParty = null }, wave), empty) &&
            ReferenceEquals(policy.Extend(empty, entry with { IsPractice = true }, wave), empty) &&
            ReferenceEquals(policy.Extend(empty, entry with { QuestId = 7 }, wave), empty),
            "前波、普通怪、其它地区、非深渊、练习及任务场景不借本服规则发材料");
        var boss = new DungeonPlannedMonster(0, 11, 71030, 95,
            new(71030, [], "[fixed]", "[boss]", "monster/合成Molech.mob"));
        var dawnEntry = entry with { DungeonId = 530, HellParty = null };
        var dawn = policy.Extend(empty, dawnEntry, boss);
        int fragments = 0, purged = 0;
        for (int roll = 0; roll < 1000; roll++)
        {
            var results = dawn.Generate(bound => bound == 1000 ? roll : 0, now);
            int id = results.Select(row => row.Item!.ItemId).Distinct().Single();
            int amount = results.Sum(row => row.Item!.Count);
            That(id == (roll < 950 ? HarlemMaterialDropPolicy.FragmentId : HarlemMaterialDropPolicy.PurgedId) &&
                amount == (roll < 950 ? 200 : 120), "黎明950/50权重边界完整覆盖，互斥分支保留各自数量并完整拆堆");
            if (id == HarlemMaterialDropPolicy.FragmentId) fragments++; else purged++;
        }
        That(fragments == 950 && purged == 50 && dawn.MaximumObjects == 2,
            "黎明每次只选一种材料，容量取两个分支的最大值，不截断200或120");
        That(ReferenceEquals(policy.Extend(empty, dawnEntry, boss with { IsDummyMapBoss = true }), empty) &&
            ReferenceEquals(policy.Extend(empty, dawnEntry, boss with { Source = boss.Source! with { Modifiers = ["[dummy]"] } }), empty) &&
            ReferenceEquals(policy.Extend(empty, dawnEntry, boss with { MonsterId = 1 }), empty) &&
            ReferenceEquals(policy.Extend(empty, dawnEntry with { BossMap = map with { Id = 1 } }, boss), empty),
            "黎明只认真实Boss及当前Boss地图，dummy和其它演员不发Boss材料");
        foreach (int permille in new[] { 0, 1000 })
        {
            var configured = new HarlemMaterialDropPolicy(rules with { DawnPurgedPermille = permille }, [291100308], Item)
                .Extend(empty, dawnEntry, boss).Generate(_ => 0, now);
            That(configured.All(row => row.Item!.ItemId == (permille == 0 ? HarlemMaterialDropPolicy.FragmentId : HarlemMaterialDropPolicy.PurgedId)),
                "配置0和1000准确关闭另一分支，不保留零权重候选");
        }
        var disabled = new HarlemMaterialDropPolicy(HarlemMaterialDropRules.Parse("{\"Enabled\":false}"), [291100308], Item);
        That(ReferenceEquals(disabled.Extend(empty, entry, wave), empty) && ReferenceEquals(disabled.Extend(empty, dawnEntry, boss), empty),
            "显式关闭同时停用普通、星空及黎明本服材料奖励");
        var pool = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(Item(1), 1) });
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("合成缺分支数量",
            [new(1000, pool, 1, 1, ItemCounts: ImmutableDictionary<int, int>.Empty)]), "分支数量必须完整覆盖候选");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("合成超容量分支",
            [new(1000, pool, 1, 1, ItemCounts: ImmutableDictionary<int, int>.Empty.Add(1, 50001))]),
            "分支数量进入500地面对象容量校验，不能越界后截断");
        Console.WriteLine($"PASS {Count - before} 哈林本服材料规则及互斥数量断言");
    }
}
