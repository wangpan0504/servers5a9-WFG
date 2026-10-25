using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SleepyHollowMaterialDropTests
{
    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(8, 0), 311401, "map/合成断头谷.map");
        var maze = new DungeonMaze(0, 9, 1, "BBFFFFFFFFFFFFFFEE", [new(0, 0)], [map.Position], [map], []);
        var source = new DungeonRoomMonster(107010048, [107010048, 1, 0, 598, 348, 0, 1, 1], "[fixed]", "[boss]", "monster/合成领主.mob");
        var room = new DungeonRoomDefinition(map, [source], [], []);
        var catalog = new SleepyHollowRewardCatalog([(maze, room)]);
        ItemTemplate Item(int id) => new(id, id == SleepyHollowMaterialDropPolicy.UsefulJunkId ? ItemKind.Quest : ItemKind.Material,
            0, 1, 0, 100, 1, 0, 1, "[free]", ushort.MaxValue, 0, "合成断头谷材料");
        var policy = new SleepyHollowMaterialDropPolicy(catalog, Item);
        var entry = new DungeonEntryPlan(1, 0, 7322, 0, byte.MaxValue, 0, maze, map, map);
        var monster = new DungeonPlannedMonster(0, 10, source.MonsterId, 95, source) { SpawnType = 3, SourceMap = map };
        var original = new DungeonDropGenerationPlan("原掉落", []);
        var plan = policy.Extend(original, entry, monster);
        for (int roll = 0; roll < 5; roll++)
        {
            int quantityDraws = 0;
            var contents = plan.Generate(bound => { if (bound == 5) { quantityDraws++; return roll; } return 0; },
                DateTimeOffset.FromUnixTimeSeconds(1800000000));
            That(contents.Where(row => row.Item?.ItemId == SleepyHollowMaterialDropPolicy.UsefulJunkId).Sum(row => row.Item!.Count) == 9 &&
                contents.Where(row => row.Item?.ItemId == SleepyHollowMaterialDropPolicy.BirthSeedId).Sum(row => row.Item!.Count) == 10 + roll &&
                quantityDraws == 1, "五个等概率抽样点分别生成10至14芽，杂物固定9，数量只抽一次");
        }
        That(plan.MaximumObjects == 2 && plan.ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule), "材料按模板完整堆叠并标注本服来源");
        foreach (var bad in new[] { monster with { IsFriendly = true }, monster with { IsDummyMapBoss = true },
            monster with { IsBlocking = false },
            monster with { AiCharacter = new(1, [1, 0, 0, 0, 0, 0], "[monster]", "[boss]", "apc/合成.apc", 95) },
            monster with { HellWave = new(2, 216, source.MonsterId, 95, 3, 10, 0) },
            monster with { AltarActor = new(10, new(0, 1, 0, source.MonsterId, 0, 0, 0, 100, 0, 1, 1, 1, 0, 1, 1), 0, 0, 3, 95) }, monster with { Team = 0 }, monster with { ObjectKey = 1 },
            monster with { SourceOrdinal = 1 }, monster with { SourceMap = map with { Id = 2 } }, monster with { SourceMap = null }, monster with { MonsterId = 123 }, monster with { SpawnType = 0 },
            monster with { Source = source with { Path = "monster/别处.mob" } },
            monster with { Source = source with { RawValues = [107010048, 1, 0, 599, 348, 0, 1, 1] } },
            monster with { Source = source with { Modifiers = ["[dummy]"] } },
            monster with { Source = source with { ScriptedRetirement = true } },
            monster with { Source = source with { RankKind = "[normal]" } },
            monster with { Source = source with { Team = 1 } } })
            That(ReferenceEquals(policy.Extend(original, entry, bad), original), "非资源绑定真实领主不能补材料");
        foreach (var bad in new[] { entry with { DungeonId = 1 }, entry with { Difficulty = 1 }, entry with { IsPractice = true },
            entry with { RouteQuestId = 123 }, entry with { IsQuestReplay = true }, entry with { IsTutorial = true },
            entry with { BossMap = map with { Id = 2 } }, entry with { BossMap = map with { Path = "map/别处.map" } },
            entry with { Maze = maze with { SourceOrdinal = 1 } }, entry with { Maze = maze with { Greed = "AA" } },
            entry with { HellParty = new(1, map.Position) } })
            That(ReferenceEquals(policy.Extend(original, bad, monster), original), "其它副本、场景、难度、练习及伪造路线不发材料");
        That(policy.IsRewardBoss(entry with { QuestId = 6060, IsOrdinaryHunt = true }, monster), "普通路线携带收集或猎杀任务仍正常掉落");
        Reject<InvalidDataException>(() => new SleepyHollowMaterialDropPolicy(catalog, id => Item(id) with { Kind = ItemKind.Material }),
            "有用杂物须保留Quest资源类别，不能误用同名材料");
        var retained = new DungeonDropTrial(1, new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(Item(1), 1) }), 1, 1, ProbabilityDenominator: 1);
        var extended = policy.Extend(new("原有奖励", [retained]), entry, monster);
        That(ReferenceEquals(extended.Trials[0], retained) && extended.Trials.Length == 3, "只追加两种材料，不覆盖原装备金币等试验");
        Reject<InvalidDataException>(() => policy.Extend(new("满容量", [retained with { Attempts = 499 }]), entry, monster),
            "两材料计入原500地面容量，不能截断原掉落");
        Console.WriteLine($"PASS {Count - before} 断头谷本服材料数量与来源边界断言");
    }
}
