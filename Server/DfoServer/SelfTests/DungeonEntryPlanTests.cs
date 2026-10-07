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
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

        var catalog = DungeonCatalog.Load(pvf);
        var character = new Character(17, 0, "Plan", 42, new Location(38, 2, 100, 200, 0), null)
        {
            Level = 1,
            ActiveMainQuests = [new(0, 3145, 1)]
        };
        bool created = DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var plan, out string reason);
        Check(created && plan is not null && reason == "resource-plan-ready", "active 3145 creates a resource plan");
        var selected = plan ?? throw new Exception("FAIL: missing resource plan");
        Check(selected.CharacterId == 17 && selected.CharacterRevision == 42 && selected.DungeonId == 3 && selected.QuestId == 3145
            && selected.ActiveQuestSlot == 0 && selected.Difficulty == 0, "plan retains character and request identity");
        Check(selected.Maze.SourceOrdinal == 1 && selected.Maze.Width == 5 && selected.Maze.Height == 2
            && selected.StartMap.Id == 76121 && selected.StartMap.Position == new DungeonPoint(0, 1)
            && selected.BossMap.Id == 76126 && selected.BossMap.Position == new DungeonPoint(4, 0), "3145 uses its task maze instead of the ordinary maze");
        Check(character.Revision == 42 && character.Position == new Location(38, 2, 100, 200, 0)
            && character.CompletedQuests.Length == 0 && character.ActiveMainQuests.SequenceEqual([new ActiveMainQuest(0, 3145, 1)]),
            "resource selection does not mutate character progress or position");
        Check(DungeonEntryPlan.TryCreate(catalog, character, 3, 3145, 0, out var repeat, out _) && repeat == selected,
            "repeated resource selection has no new instance or state transition");

        Reject(character with { ActiveMainQuests = [] }, 3, 3145, 0, "quest-not-active");
        Reject(character with { CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [], CompletedQuests = [3145] }, 3, 3145, 0, "quest-already-completed");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 0)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, uint.MaxValue)] }, 3, 3145, 0, "quest-condition-not-pending");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(1, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(0, 3145, 1), new(0, 3146, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { ActiveMainQuests = [new(30, 3145, 1)] }, 3, 3145, 0, "quest-active-conflict");
        Reject(character with { Position = character.Position with { Town = 1 } }, 3, 3145, 0, "dungeon-entry-mismatch");
        Reject(character with { Position = character.Position with { Area = 4 } }, 3, 3145, 0, "dungeon-entry-mismatch");
        Reject(character with { Level = 0 }, 3, 3145, 0, "character-level-invalid");
        Reject(character with { Level = 96 }, 3, 3145, 0, "character-level-invalid");
        Reject(character, 0, 3145, 0, "dungeon-unsupported");
        Reject(character, uint.MaxValue, 3145, 0, "dungeon-unsupported");
        Reject(character, 3, 0, 0, "quest-unsupported");
        Reject(character, 3, 3146, 0, "quest-unsupported");
        Reject(character, 3, 3145, 1, "difficulty-unsupported");
        Reject(character, 3, 3145, byte.MaxValue, "difficulty-unsupported");

        var definition = catalog.Get(3);
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
        Check(DungeonEntryPlan.TryCreate(definition with { Mazes = [movedMaze] }, character, 3, 3145, 0, out var moved, out _)
            && moved is not null && moved.StartMap == fixtureStart && moved.BossMap == fixtureBoss && moved.Maze.SourceOrdinal == 7,
            "plan derives map IDs, coordinates and source ordinal from resource definitions");
        Console.WriteLine($"PASS {checks} dungeon entry plan assertions");

        void Reject(Character input, uint dungeonId, uint questId, byte difficulty, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(catalog, input, dungeonId, questId, difficulty, out var rejected, out string actual);
            Check(!accepted && rejected is null && actual == expected, "resource request rejects " + expected);
        }

        void RejectDefinition(DungeonDefinition input, string expected)
        {
            bool accepted = DungeonEntryPlan.TryCreate(input, character, 3, 3145, 0, out var rejected, out string actual);
            Check(!accepted && rejected is null && actual == expected, "resource definition rejects " + expected);
        }

        void RejectMaze(DungeonMaze input, string expected) => RejectDefinition(definition with { Mazes = [input] }, expected);
    }
}
