using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonDropControlTests
{
    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(0, 0), 3, "map/control.map");
        var maze = new DungeonMaze(0, 1, 1, "", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 51, 0, 0, 0, maze, map, map);
        var global = new DungeonDropMultipliers(Gold: 2);
        var ordinary = new DungeonDropMultipliers(Equipment: 3);
        var story = new DungeonDropMultipliers(Materials: 4);
        var raid = new DungeonDropMultipliers(Special: 5);
        var settings = new DungeonDropControlSettings();
        settings.Replace([new(DungeonDropScope.All, [], global), new(DungeonDropScope.Ordinary, [51], ordinary),
            new(DungeonDropScope.Story, [], story), new(DungeonDropScope.Raid, [], raid)], true);
        var snapshot = settings.Current;
        That(snapshot.Resolve(entry) == ordinary && snapshot.Resolve(entry with { DungeonId = 52 }) == global,
            "白图按真实身份和显式副本集合匹配，集合外回退全局");
        That(snapshot.Resolve(entry with { QuestId = 10, RouteQuestId = 10 }) == story &&
            snapshot.Resolve(entry with { RaidEntry = new(Guid.NewGuid(), 1, 0) }) == raid,
            "剧情根据任务路线，攻坚根据正式RaidEntry，范围规则覆盖全局");
        foreach (var excluded in new[] { entry with { IsPractice = true }, entry with { IsTutorial = true },
            entry with { IsBossRush = true }, entry with { IsTagEvildom = true },
            entry with { DungeonId = 11006 }, entry with { DungeonId = 11008 } })
            That(snapshot.Resolve(excluded) == global, "专用玩法不冒充白图范围");
        settings.Replace([], false);
        That(settings.Current.Resolve(entry) is null && snapshot.Resolve(entry) == ordinary,
            "重载发布新快照，已入场引用保持旧规则");
        foreach (var invalid in new DungeonDropControlRule[][]
        {
            [new(DungeonDropScope.All, [], global), new(DungeonDropScope.All, [51], ordinary)],
            [new((DungeonDropScope)99, [], global)], [new(DungeonDropScope.All, [0], global)],
            [new(DungeonDropScope.All, [51, 51], global)], [new(DungeonDropScope.All, [], new(Gold: -1))],
            [new(DungeonDropScope.All, [], new(Special: 100.01m))]
        })
        {
            var previous = settings.Current;
            Reject<InvalidDataException>(() => settings.Replace(invalid, true), "非法掉落配置整份拒绝");
            That(ReferenceEquals(settings.Current, previous), "非法重载不替换旧快照");
        }
        var item = new ItemTemplate(1000, ItemKind.Consumable, 0, 1, 0, 99, 1, 0, 1, "[free]", ushort.MaxValue, 0, "测试材料");
        var pool = new DungeonItemDropPool([new DungeonWeightedDropItem(item, 1)]);
        var plan = new DungeonDropGenerationPlan("控制实际生成", [new(200000, pool, 2, 2, ProbabilityDenominator: 1000000,
            Category: DungeonDropCategory.Materials)]) { OrdinaryLukeDailySource = 123 };
        That(ReferenceEquals(DungeonDropControlSnapshot.Apply(plan, null), plan), "关闭不添加规则或消耗随机");
        var original = DungeonDropControlSnapshot.Apply(plan, new());
        That(original.Trials.SequenceEqual(plan.Trials) && original.OrdinaryLukeDailySource == 123 &&
            original.MaximumObjects == plan.MaximumObjects, "1倍率保留原概率、数量、资格及容量");
        var twice = DungeonDropControlSnapshot.Apply(plan, new(Materials: 2));
        int roll = 300000;
        var now = DateTimeOffset.UnixEpoch;
        That(plan.Generate(bound => bound == 1000000 ? roll : 0, now).IsEmpty &&
            twice.Generate(bound => bound == 1000000 ? roll : 0, now) is [{ Item.Count: 2 }],
            "相同真实随机抽样在倍率边界改变命中，仍由正式生成器生成物品");
        That(DungeonDropControlSnapshot.Apply(plan, new(Materials: 0)).Generate(_ => 0, now).IsEmpty &&
            DungeonDropControlSnapshot.Apply(plan, new(Materials: 100)).Trials[0].Threshold == 1000000 &&
            twice.Trials[0].Items == plan.Trials[0].Items && twice.Trials[0].MaximumCount == 2,
            "0倍率不掉落，超过分母封顶，原候选与数量保持");
        var all = new DungeonDropGenerationPlan("各类独立", Enum.GetValues<DungeonDropCategory>()
            .Select(category => new DungeonDropTrial(1000, pool, 1, 1, Category: category)));
        var controlled = DungeonDropControlSnapshot.Apply(all, new(2, 3, 4, 5, 6));
        That(controlled.Trials.Select(row => row.Threshold).SequenceEqual(new[] { 6000, 2000, 3000, 4000, 5000 }),
            "五类倍率各自进入正式阈值，不相互串用");
        var listing = DungeonDropListing.Rows(twice).Single();
        That(listing.ItemId == item.Id && listing.Probability == 0.4m && listing.MinimumCount == 2 && listing.MaximumCount == 2,
            "逐物品清单直接读取正式调整后阈值与数量");
        var weighted = new DungeonItemDropPool([new DungeonWeightedDropItem(item, 1), new(item with { Id = 1001 }, 3)]);
        var branched = new DungeonDropGenerationPlan("稀有度清单", [new(DungeonDropRatePlan.ProbabilityDenominator, null, 1, 1,
            RarityPools: [new(500000, weighted, pool, 250), new(1000000, pool)], Attempts: 2)]);
        var listed = DungeonDropListing.Rows(branched).ToArray();
        That(listed.Sum(row => row.Probability) == 1 && listed[0].Probability == 0.09375m && listed[1].Probability == 0.28125m &&
            listed[2].Probability == 0.125m && listed.All(row => row.Attempts == 2),
            "清单保留稀有度区间、互斥分支与候选权重；每次概率不冒充多次至少一次概率");
        VerifyEquipmentAndFilters();
        VerifyDirectEpics();
        foreach (var factor in new[] { 0m, 0.5m, 1m, 2m, 100m })
        {
            var comparison = DungeonDropListing.ComparisonRows(plan, new(Materials: factor)).Single();
            That(comparison.OriginalProbability == 0.2m && comparison.Multiplier == factor &&
                comparison.Probability == Math.Min(1m, 0.2m * factor), "零值及封顶仍保留真实原概率与已保存倍率");
        }
        var compared = DungeonDropListing.ComparisonRows(branched, new(Special: 2)).ToArray();
        That(compared.Length == listed.Length && compared.Zip(listed).All(pair =>
            pair.First.OriginalProbability == pair.Second.Probability && pair.First.ItemId == pair.Second.ItemId &&
            pair.First.Channel == pair.Second.Channel && pair.First.Attempts == pair.Second.Attempts),
            "重复物品及不同稀有度分支逐行对应，不按物品编号合并原概率");
        VerifyFile();
        Console.WriteLine($"PASS drop control: {Count - before} checks");
    }

    static void VerifyFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "drop-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new DungeonDropConfiguration(directory);
            config.Reload();
            That(!config.Settings.Current.Enabled && config.Answer("other") is null, "缺省关闭，不截获其他命令");
            var path = Path.Combine(directory, "drop-control.json");
            File.WriteAllText(path, """{"Enabled":true,"Rules":[{"Scope":"All","DungeonIds":[],"Multipliers":{"Gold":2}}]}""");
            That(config.Answer("drop-control reload")!.StartsWith("OK ") && config.Settings.Current.Enabled &&
                config.Settings.Current.Rules[0].Multipliers.Gold == 2 && config.Settings.Current.Rules[0].Multipliers.Materials == 1,
                "真实配置热重载，缺省类别保持1倍率");
            var previous = config.Settings.Current;
            foreach (var json in new[] { """{"Enabled":true,"Rules":[],"Typo":1}""",
                """{"Enabled":true,"Rules":[{"Scope":0,"DungeonIds":[],"Multipliers":{}}]}""",
                """{"Enabled":true,"Rules":[{"Scope":"All","DungeonIds":[],"Multipliers":{"Gold":101}}]}""" })
            {
                File.WriteAllText(path, json);
                That(config.Answer("drop-control reload")!.StartsWith("ERR ") && ReferenceEquals(previous, config.Settings.Current),
                    "未知字段、数字范围及越界倍率拒绝并保留旧配置");
            }
            That(config.Answer("drop-control status")!.Contains("\"version\":2") &&
                config.Answer("drop-control reload extra")!.StartsWith("ERR "), "查询包含能力版本，命令参数严格验证");
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
    }
}
