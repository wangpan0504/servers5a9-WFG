using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer;

internal static class DungeonEntryPlanTests
{
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;

        var catalog = DungeonCatalog.Load(pvf);
        var character = new Character(17, 0, "Plan", 42, new Location(38, 2, 100, 200, 0), null)
        {
            Level = 1,
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        bool created = DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var plan, out string reason);
        That(created && plan is not null && reason == "resource-plan-ready", "active 3145 creates a resource plan");
        var selected = plan ?? throw new Exception("FAIL: missing resource plan");
        That(selected.CharacterId == 17 && selected.CharacterRevision == 42 && selected.DungeonId == 3 && selected.QuestId == 3145
            && selected.ActiveQuestSlot == 0 && selected.Difficulty == 0, "plan retains character and request identity");
        That(selected.Maze.SourceOrdinal == 1 && selected.Maze.Width == 5 && selected.Maze.Height == 2
            && selected.StartMap.Id == 76121 && selected.StartMap.Position == new DungeonPoint(0, 1)
            && selected.BossMap.Id == 76126 && selected.BossMap.Position == new DungeonPoint(4, 0), "3145 uses its task maze instead of the ordinary maze");
        That(character.Revision == 42 && character.Position == new Location(38, 2, 100, 200, 0)
            && character.CompletedQuests.Length == 0 && character.ActiveMainQuests.SequenceEqual([new ActiveMainQuest(0, 3145, 1)]),
            "resource selection does not mutate character progress or position");
        That(DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var repeat, out _) && repeat is not null &&
            repeat.QuestAcceptances.SequenceEqual(selected.QuestAcceptances) && repeat with { QuestAcceptances = selected.QuestAcceptances } == selected,
            "repeated resource selection has no new instance or state transition");
        character.ActiveMainQuests[0] = new(0,3145,0);
        That(selected.QuestAcceptances is [{ ConditionState: 1 }], "入场冻结任务值，后续角色数组变化不改写旧实例接取快照");
        character.ActiveMainQuests[0] = new(0,3145,1);

        Reject(character with { ActiveMainQuests = [] }, 3, 3145, 0, "quest-not-active");
        Reject(character with { CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [], CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 0)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, uint.MaxValue)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(1, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(0, 3146, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(30, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { Level = 0 }, 3, 3145, 0, "character-level-invalid");
        Reject(character with { Level = 96 }, 3, 3145, 0, "character-level-invalid");
        Reject(character, 0, 3145, 0, "dungeon-unsupported");
        Reject(character, uint.MaxValue, 3145, 0, "dungeon-unsupported");
        Reject(character, 3, 0, 0, "quest-route-definition-unavailable");
        Reject(character, 3, 3146, 0, "quest-not-active");
        Reject(character, 3, 3145, 1, "difficulty-unsupported");
        Reject(character, 3, 3145, byte.MaxValue, "difficulty-unsupported");

        var definition = catalog.Get(3);
        var quests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf);
        That(DungeonEntryPlan.TryCreate(catalog,character,3,0,0,out var automatic,out _,quests: quests) &&
            automatic!.QuestId == 0 && automatic.RouteQuestId == 3145 && automatic.Maze.SourceOrdinal == 1 && automatic.QuestAcceptanceId == Guid.Empty,
            "普通选图请求优先选择唯一活动任务的资源路线");
        var noTask = character with { ActiveMainQuests = [], CompletedQuests = [3145] };
        That(DungeonEntryPlan.TryCreate(catalog,noTask,3,0,0,out var ordinary,out _,draw: _ => 0,quests: quests) && ordinary is
            { QuestId: 0, RouteQuestId: 0, ActiveQuestSlot: byte.MaxValue, Maze.SourceOrdinal: 0, StartMap.Id: 58548, BossMap.Id: 58553 },
            "没有剧情路线时使用PVF普通迷宫，不能把旧任务接取伪造成当前活动任务");
        That(!DungeonEntryPlan.TryCreate(catalog,character with { ActiveMainQuests = [new(0,3145,1),new(1,3146,2)] },
            3,0,0,out _,out string ambiguous,quests: quests) && ambiguous == "quest-maze-ambiguous",
            "同时匹配不同任务路线时拒绝进入，不随意替换地图");
        That(catalog.Get(12).AllowsDifficulty(1,false) && !catalog.Get(12).AllowsDifficulty(2,false) &&
            !catalog.Get(12).AllowsDifficulty(1,true) && !catalog.Get(3).AllowsDifficulty(1,false),
            "合法普通难度按difficulty level检查，剧情difficulty size及designate限制仍保留");
        var maze = selected.Maze;
        RejectDefinition(definition with { Id = 1 }, "dungeon-resource-mismatch");
        RejectDefinition(definition with { MinimumLevel = null }, "dungeon-level-unknown");
        RejectDefinition(definition with { MinimumLevel = 2 }, "dungeon-level-too-low");
        RejectDefinition(definition with { Mazes = [definition.Mazes[0]] }, "quest-maze-unavailable");
        RejectDefinition(definition with { Mazes = [maze, maze] }, "quest-maze-ambiguous");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 0), new(7, 3145), new(0, -1)] }, "quest-maze-unavailable");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 0), new(0, 3145)] }, "quest-maze-unavailable");
        RejectMaze(maze with { QuestConnectionTokens = [new(0, 1), new(0, 3145), new(0, -1)] }, "quest-maze-unavailable");
        RejectMaze(maze with { MapSpecification = null }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Starts = [] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Starts = [maze.Starts[0], maze.Starts[0]] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Bosses = [] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { Bosses = [new DungeonPoint(maze.Width, 0)] }, "quest-maze-layout-unsupported");
        RejectMaze(maze with { MapSpecification = maze.MapSpecification!.Where(map => map.Id != selected.StartMap.Id).ToArray() }, "quest-maze-map-unavailable");
        RejectMaze(maze with { MapSpecification = [.. maze.MapSpecification!, selected.StartMap] }, "quest-maze-map-unavailable");
        RejectMaze(maze with { MapSpecification = maze.MapSpecification!.Select(map => map == selected.BossMap ? map with { Kind = "map" } : map).ToArray() }, "quest-maze-map-unavailable");

        // 改动 fixture 的资源坐标与编号，证明计划使用输入定义而非内嵌的地图或坐标常量。
        var fixtureStart = new DungeonMap("map", new(1, 0), 123, "map/fixture-start.map");
        var fixtureBoss = new DungeonMap("boss", new(0, 0), 456, "map/fixture-boss.map");
        var movedMaze = maze with { SourceOrdinal = 7, Width = 2, Height = 1, Starts = [fixtureStart.Position], Bosses = [fixtureBoss.Position], MapSpecification = [fixtureStart, fixtureBoss] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [movedMaze] }, character, 3, 3145, 0, out var moved, out _)
            && moved is not null && moved.StartMap == fixtureStart && moved.BossMap == fixtureBoss && moved.Maze.SourceOrdinal == 7,
            "plan derives map IDs, coordinates and source ordinal from resource definitions");
        var parent = QuestEligibilityTests.Quest(100) with { Condition = "[quest clear]", SubType = 2, IntData = [101,102] };
        var otherMaze = movedMaze with { QuestConnectionTokens = [new(0,0),new(0,100),new(0,-1)] };
        var otherCharacter = character with { ActiveMainQuests = [new(0,100,2)] };
        That(DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze] },otherCharacter,17,100,0,out var general,out _,quest: parent) &&
            general is { QuestId: 100, DungeonId: 17, BossMap.Id: 456 }, "不同副本和任务号、父聚合初值2按显式资源路线生成");
        var clearTarget = parent with { Condition = "[clear map]", SubType = -1, IntData = [999] };
        That(!DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze] },otherCharacter with { ActiveMainQuests = [new(0,100,1)] },
            17,100,0,out _,out var mismatch,quest: clearTarget) && mismatch == "quest-terminal-map-unavailable", "任务目标地图不属于路线Boss时拒绝，不替换路线");
        Console.WriteLine($"PASS {Count - checks} dungeon entry plan assertions");

        void Reject(Character input, uint dungeonId, uint questId, byte difficulty, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(catalog, input, dungeonId, questId, difficulty, out var rejected, out string actual);
            That(!accepted && rejected is null && actual == expected, "resource request rejects " + expected);
        }

        void RejectDefinition(DungeonDefinition input, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(input, character, 3, 3145, 0, out var rejected, out string actual);
            That(!accepted && rejected is null && actual == expected, "resource definition rejects " + expected);
        }

        void RejectMaze(DungeonMaze input, string expected) => RejectDefinition(definition with { Mazes = [input] }, expected);
    }
}
