using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonChampionPolicyTests
{
    internal static void RunResources(PvfArchive pvf)
    {
        int checks = Count;
        DungeonCatalogTests.VerifyChampionRates();
        var source = new DungeonRoomMonster(1, [1, 0, 3, 100, 100, 0, 1, 1],
            "[fixed]", "[normal]", "monster/fixture.mob");
        DungeonMonsterPlanTests.VerifyChampionRolls(source);
        SinglePlayerDungeonRunTests.RunChampionCore();
        var catalog = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var character = new Character(99, 0, "Champion", 0, new(38, 1, 1, 1), null) { Level = 95 };
        // 资源使用各自正式索引；准入仅借用已实现的真实kind，不加载整套攻坚阶段测试。
        foreach (var (id, kind, guide) in new (int, RaidKind?, bool)[]
        {
            (5101, null, false), (400000035, null, false),
            (5054, RaidKind.Luke, false), (5350, RaidKind.Fiendwar, true),
            (4991, RaidKind.Prey, false), (291100386, RaidKind.Prey, true)
        })
        {
            var definition = catalog.Get(id);
            var identity = kind is { } raidKind ? new RaidDungeonIdentity(Guid.NewGuid(), 0,
                raidKind == RaidKind.Prey && !guide ? (byte)0 : (byte)1, raidKind)
                { PreyGuide = guide, PreySoloCharacter = raidKind == RaidKind.Prey && !guide ? character.Id : null } : null;
            var maze = definition.Mazes.First(row => row.QuestConnectionTokens.Count == 0);
            That(DungeonEntryPlan.TryCreate(definition, character, (uint)id, 0, 2, out var entry, out var reason,
                draw: _ => 0, resolveMaze: row => catalog.ResolveOrdinaryMaze(definition, row, _ => 0),
                raid: identity, raidMaze: identity is null ? null : maze.SourceOrdinal),
                "普通卢克/普雷及已实现团本引导建立真实资源Entry：" + id + "/" + reason);
            That(entry!.ChampionPercents.IsEnabled && entry.ChampionPercents ==
                DungeonChampionRates.For(definition, raidAdmission: identity is not null),
                "Entry不再吞掉匹配资源正概率：" + id);
            That(DungeonEntryPlan.TryCreate(definition, character with { Id = 100 }, (uint)id, 0, entry.Difficulty,
                out var shared, out _, partyRoute: entry, raid: identity, raidMaze: maze.SourceOrdinal,
                draw: _ => throw new InvalidOperationException("共享Entry不应重抽")) &&
                ReferenceEquals(shared!.Maze, entry.Maze) && shared.ChampionPercents == entry.ChampionPercents,
                "成员沿队长冻结路线和概率，不另抽迷宫：" + id);
            bool hasCandidate = false;
            foreach (var map in entry.Maze.MapSpecification!)
            {
                var plan = DungeonMonsterPlan.Create(rooms, map, character.Id, 100, entry.MonsterBasisLevel, entry.PlayerTeam);
                var rolled = plan.WithChampionRolls(entry.ChampionPercents, entry.Difficulty, _ => 0, entry.NamedMonsters);
                int changed = plan.Monsters.Zip(rolled.Monsters).Count(pair => pair.First.SpawnType != pair.Second.SpawnType);
                hasCandidate |= changed != 0;
                That(changed <= 1 && plan.Monsters.Zip(rolled.Monsters).All(pair =>
                    pair.Second == pair.First || pair.Second == pair.First with { SpawnType = 1 }),
                    "真实房间至多提升一只，保留原key/等级/来源及显式品级：" + id + "/" + map.Id);
                That(ReferenceEquals(plan.WithChampionRolls(entry.ChampionPercents, entry.Difficulty,
                    maximum => maximum == 100 ? 99 : 0, entry.NamedMonsters), plan),
                    "真实资源概率拒绝骰保持原计划：" + id + "/" + map.Id);
            }
            if (id == 5350)
                That(hasCandidate, "漩涡引导已证九只普通演员的真实地图命中骰可提升一只");
            if (identity is not null)
                That(!DungeonEntryPlan.TryCreate(definition, character, (uint)id, 0, 2, out _, out _,
                    raid: identity with { InstanceId = Guid.Empty }, raidMaze: maze.SourceOrdinal),
                    "开放精英概率不绕过正式团本实例身份门禁：" + id);
        }
        foreach (int id in new[] { 4988, 4989, 4990, 291100382, 291100384, 291100385 })
        {
            var definition = catalog.Get(id);
            foreach (var map in definition.Mazes.First(row => row.QuestConnectionTokens.Count == 0).MapSpecification!)
            {
                var plan = DungeonMonsterPlan.Create(rooms, map, 99, 100, definition.BasisLevel, 0);
                var rolled = plan.WithChampionRolls(new(100, 100, 100, 100), 2,
                    _ => throw new InvalidOperationException("防御战固定演员无精英候选，不应掷骰"));
                That(ReferenceEquals(plan, rolled), "六个真实防御战保留no champion普通演员和Boss：" + id + "/" + map.Id);
            }
        }
        That(!DungeonChampionRates.For(catalog.Get(291100320)).IsEnabled,
            "泰波尔斯全零资源保持无随机精英，不以类型猜例外");
        Console.WriteLine($"PASS {Count - checks} champion policy assertions");
    }
}
