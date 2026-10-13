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
        foreach (var ordinaryCharacter in new[]
        {
            character,
            character with { ActiveMainQuests = [], CompletedQuests = [3145] },
            character with { ActiveMainQuests = [] }
        })
        {
            That(DungeonEntryPlan.TryCreate(catalog, ordinaryCharacter, 3, 0, 0, out var normalPlan, out _) &&
                normalPlan is { IsOrdinary: true, QuestAcceptanceId: var acceptance, ActiveQuestSlot: byte.MaxValue } &&
                acceptance == Guid.Empty && normalPlan.Maze.SourceOrdinal == 0 && normalPlan.StartMap.Id == 58548 && normalPlan.BossMap.Id == 58553,
                "未完成、已完成和无活动主线均选择真正普通maze，不伪造任务身份");
        }
        foreach (int id in new[] { 3, 5, 6, 7, 8, 9, 1000 })
        {
            var resource = catalog.Get(id);
            var eligible = character with { Level = checked((byte)resource.MinimumLevel!.Value), ActiveMainQuests = [] };
            That(DungeonEntryPlan.TryCreate(catalog, eligible, checked((uint)id), 0, 0, out var normalPlan, out _) &&
                normalPlan!.Maze.StaticTopology.Supported && normalPlan.Maze.QuestConnectionTokens.Count == 0 &&
                normalPlan.MonsterBasisLevel == resource.BasisLevel, "左入口各普通图使用其资源等级、路线和难度");
            Reject(eligible, checked((uint)id), 0, 1, "difficulty-unsupported");
            if (eligible.Level > 1)
                Reject(eligible with { Level = (byte)(eligible.Level - 1) }, checked((uint)id), 0, 0, "dungeon-level-too-low");
        }
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
        var hero = character with { Job = 13, GrowType = 36, Level = 90, ActiveMainQuests = [new(3,3240,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,hero,151,3240,0,out var heroPlan,out string heroReason,quest: quests.Get(3240),quests: quests),
            "向暗黑城前进可进入英雄冢：" + heroReason);
        That(heroPlan is { QuestId: 3240, RouteQuestId: 3240, ActiveQuestSlot: 3, Maze.SourceOrdinal: 7, StartMap.Id: 91391, BossMap.Id: 91398 } &&
            heroPlan.QuestAcceptanceId == hero.ActiveMainQuests[0].AcceptanceId && heroPlan.Maze.StaticTopology.Supported,
            "英雄冢沿真实任务接取身份及八房路线规划，不替换成普通图");
        That(DungeonEntryPlan.TryCreate(catalog,hero,151,0,0,out var autoHero,out _,quests: quests) &&
            autoHero is { QuestId: 0, RouteQuestId: 3240, Maze.SourceOrdinal: 7 }, "普通选图同样匹配英雄冢活动任务路线");
        var noir = hero with { Job = 0, GrowType = 0, CompletedQuests = [3272,5149],
            ActiveMainQuests = [new(5,3273,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,noir,239,3273,0,out var noirParent,out string noirReason,
            quest: quests.Get(3273),quests: quests), "真实抵达诺伊佩拉父请求选择当前子路线：" + noirReason);
        That(noirParent is { QuestId: 3273, RouteQuestId: 3273, ActiveQuestSlot: 5, StartMap.Id: 70148, BossMap.Id: 70154 } &&
            noirParent.QuestAcceptanceId == noir.ActiveMainQuests[0].AcceptanceId && noirParent.QuestAcceptances.SequenceEqual(noir.ActiveMainQuests),
            "父身份与持久接取保留，只借用5150迷宫，不在入场规划中接取子任务");
        That(DungeonEntryPlan.TryCreate(catalog,noir,239,0,0,out var noirAutomatic,out _,quests: quests) &&
            noirAutomatic is { QuestId: 0, RouteQuestId: 3273, BossMap.Id: 70154, QuestAcceptanceId: var noAcceptance } && noAcceptance == Guid.Empty,
            "普通0请求也能由已接父任务和真实子完成记录选择诺伊佩拉路线，不伪造请求任务身份");
        That(!DungeonEntryPlan.TryCreate(catalog,noir with { CompletedQuests = [3272], ActiveMainQuests = [noir.ActiveMainQuests[0] with { ConditionState = 2 }] },
            239,3273,0,out _,out _,quest: quests.Get(3273),quests: quests), "5149材料步骤未完成时不跳到5150迷宫");
        var heroDefinition = catalog.Get(151);
        var heroLevels = heroDefinition.RawSections.First(section => section.Tag == "difficulty level");
        That(heroDefinition.RawSections.Count(section => section.Tag == "difficulty level") == 2 &&
            Enumerable.Range(0,5).All(value => heroDefinition.AllowsDifficulty((byte)value,false) == (value < 3)) &&
            !heroDefinition.AllowsDifficulty(1,true), "相同重复难度表保留普通难度范围与剧情难度限制");
        foreach (var tokens in new PvfToken[][]
        {
            [new(0,0),new(0,1),new(0,1),new(0,0),new(0,0)],
            [new(0,1)],
            [new(0,1),new(0,1),new(0,2),new(0,0),new(0,0)],
            [new(7,1),new(0,1),new(0,1),new(0,0),new(0,0)]
        })
            That(!(heroDefinition with { RawSections = [heroLevels,new("difficulty level",tokens)] }).AllowsDifficulty(0,false),
                "冲突、残缺、非法值与引用难度表仍拒绝");
        var maze = selected.Maze;
        RejectDefinition(definition with { Id = 1 }, "dungeon-resource-mismatch");
        RejectDefinition(definition with { MinimumLevel = null }, "dungeon-level-unknown");
        RejectDefinition(definition with { MinimumLevel = 2 }, "dungeon-level-too-low");
        RejectDefinition(definition with { Mazes = [definition.Mazes[0]] }, "quest-maze-unavailable");
        var duplicate = maze with { SourceOrdinal = maze.SourceOrdinal + 1 };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze, duplicate] },character,3,3145,0,out var deduplicated,out _) &&
            deduplicated!.Maze == maze, "显式任务的完整等价路线保留首次声明");
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze, duplicate] },character,3,0,0,out var autoDuplicate,out _,quests: quests) &&
            autoDuplicate is { RouteQuestId: 3145, Maze.SourceOrdinal: 1 }, "普通选图的活动任务路线也去重");
        var completedMaze = maze with { QuestConnectionTokens = [new(0,1),new(0,3145),new(0,-1)] };
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [completedMaze,completedMaze with { SourceOrdinal = 9 }] },
            character with { ActiveMainQuests = [], CompletedQuests = [3145] },3,0,0,out var completedRoute,out _,quests: quests) &&
            completedRoute!.RouteQuestId == 3145, "普通选图的完成任务路线也去重");
        foreach (var different in new[]
        {
            maze with { ResourceError = "fixture" },
            maze with { ClearConditions = [new("hunt monster",999,1)] },
            maze with { WarpMaps = [new(maze.Starts[0],maze.Bosses[0])] },
            maze with { BossEntrance = new(0,0,[new(999,0,1)]) },
            maze with { MapSpecification = maze.MapSpecification!.Select((map,index) => index == 0 ? map with { CandidateIds = [map.Id,999] } : map).ToArray() }
        })
        {
            RejectDefinition(definition with { Mazes = [maze,different] }, "quest-maze-ambiguous");
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,duplicate,different] },character,3,0,0,out _,out var mixed,quests: quests) &&
                mixed == "quest-maze-ambiguous", "重复项不能掩盖不同路线的真实歧义");
        }
        var differentQuest = maze with { QuestConnectionTokens = [new(0,0),new(0,3146),new(0,-1)] };
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,differentQuest] },
            character with { ActiveMainQuests = [new(0,3145,1),new(1,3146,1)] },3,0,0,out _,out _,quests: quests),
            "地图相同但任务身份不同仍拒绝歧义");
        var difficultyRoute = maze with { QuestConnectionTokens = [new(0,0),new(0,3145),new(0,0)] };
        RejectDefinition(definition with { Mazes = [maze,difficultyRoute] }, "quest-maze-ambiguous");

        foreach (ushort id in new ushort[] { 7104,7105 })
        {
            var actual = character with { Level = 20, GrowType = 5, ActiveMainQuests = [new(0,id,1)] };
            That(DungeonEntryPlan.TryCreate(catalog,actual,1000,id,0,out _,out _,quest: quests.Get(id),quests: quests) &&
                DungeonEntryPlan.TryCreate(catalog,actual,1000,0,0,out var routed,out _,quests: quests) && routed!.RouteQuestId == id,
                "匹配剑影转职资源的显式和普通选图入口均可规划");
        }
        var vagabond = quests.Get(13102);
        var candidate = character with { Job = 11, Level = 15, GrowType = 0, CompletedQuests = [3155],
            ActiveMainQuests = [new(0,13102,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,candidate,7143,13102,0,out var vagabondPlan,out var vagabondReason,
            quest: vagabond,quests: quests), "流浪武士转职使用资源声明的无连接迷宫: " + vagabondReason);
        That(vagabondPlan is { QuestId: 13102, RouteQuestId: 13102, StartMap.Id: 76115, BossMap.Id: 76118 } &&
            vagabondPlan.QuestAcceptanceId == candidate.ActiveMainQuests[0].AcceptanceId && vagabondPlan.Maze.StaticTopology.Supported,
            "转职路线保留原任务接取身份、真实四房终点和可走拓扑");
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
        var child = parent with { Id = 101, MainQuest = 100 };
        var unlinkedMaze = otherMaze with { QuestConnectionTokens = [] };
        var unlinkedDefinition = definition with { Id = 17, Mazes = [unlinkedMaze] };
        var unlinkedQuest = clearTarget with { IntData = [456], Definition = clearTarget.Definition! with { Dungeons = [new(17,-1)] } };
        var pendingClear = otherCharacter with { ActiveMainQuests = [new(0,100,1)] };
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out var unlinked,out _,quest: unlinkedQuest) &&
            unlinked is { QuestId: 100, RouteQuestId: 100, BossMap.Id: 456 },
            "合成不同任务和地图同样复用明确声明的唯一无连接清图路线，不硬编码转职编号");
        foreach (var unsupported in new[] { unlinkedQuest with { Definition = unlinkedQuest.Definition! with { Dungeons = [] } },
            unlinkedQuest with { Definition = unlinkedQuest.Definition! with { Dungeons = [new(18,-1)] } },
            unlinkedQuest with { Condition = "[hunt enemy]" } })
            That(!DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out _,out _,quest: unsupported),
                "缺少地下城归属或非清图目标不能借用无连接路线");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out _,out var wrongTarget,
                quest: unlinkedQuest with { IntData = [999] }) && wrongTarget == "quest-terminal-map-unavailable" &&
            !DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [unlinkedMaze,unlinkedMaze with { SourceOrdinal = 8 }] },
                pendingClear,17,100,0,out _,out var multiple,quest: unlinkedQuest) && multiple == "quest-maze-ambiguous",
            "无连接路线仍核对目标且拒绝多个候选，不猜测地图或随机绕过歧义");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [unlinkedMaze,otherMaze with {
                QuestConnectionTokens = [new(0,0),new(0,100),new(0,1)] }] },pendingClear,17,100,0,out _,out _,quest: unlinkedQuest) &&
            !DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear with { ActiveMainQuests = [] },17,100,0,out _,out _,quest: unlinkedQuest),
            "既有任务连接的难度不符和未接取不能被无连接回退放行");
        That(DungeonEntryPlan.TryCreate(definition with { Id = 17, Mazes = [otherMaze,otherMaze with { SourceOrdinal = 8 }] },
            otherCharacter with { ActiveMainQuests = [new(0,101,1)] },17,101,0,out var fallback,out _,quest: child) &&
            fallback!.RouteQuestId == 100, "子任务回退到父任务的重复路线也去重");
        var sequentialChild = unlinkedQuest with { Id = 102, MainQuest = 100 };
        var sequentialCatalog = new DfoServer.GameWorld.Quests.QuestCatalog(new()
            { [100] = parent, [101] = QuestEligibilityTests.Quest(101) with { MainQuest = 100 }, [102] = sequentialChild });
        var childMaze = otherMaze with { QuestConnectionTokens = [new(0,0),new(0,102),new(0,-1)] };
        var childDefinition = definition with { Id = 17, Mazes = [childMaze] };
        var sequence = otherCharacter with { CompletedQuests = [101],
            ActiveMainQuests = [new(5,100,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(childDefinition,sequence,17,100,0,out var parentRoute,out _,quest: parent,quests: sequentialCatalog) &&
            parentRoute is { QuestId: 100, RouteQuestId: 100, ActiveQuestSlot: 5, BossMap.Id: 456 } &&
            parentRoute.QuestAcceptances.SequenceEqual(sequence.ActiveMainQuests),
            "不同父子编号同样按真实完成集选择当前子路线，保留父请求身份和接取快照");
        var childAccepted = sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0],new(6,102,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(childDefinition,childAccepted,17,100,0,out var acceptedRoute,out _,quest: parent,quests: sequentialCatalog) &&
            acceptedRoute!.QuestAcceptances.SequenceEqual(childAccepted.ActiveMainQuests) &&
            DungeonEntryPlan.TryCreate(childDefinition,childAccepted,17,102,0,out var directChild,out _,quest: sequentialChild,quests: sequentialCatalog) &&
            directChild is { QuestId: 102, BossMap.Id: 456 }, "已接取子任务保留自己的身份，父/子两种真实请求都匹配相同路线");
        foreach (var invalid in new[] { sequence with { CompletedQuests = [] }, sequence with { CompletedQuests = [101,102] },
            sequence with { CompletedQuests = [100,101] }, sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0] with { ConditionState = 2 }] },
            sequence with { ActiveMainQuests = [sequence.ActiveMainQuests[0] with { AcceptanceId = Guid.Empty }] },
            childAccepted with { ActiveMainQuests = [sequence.ActiveMainQuests[0],childAccepted.ActiveMainQuests[1] with { ConditionState = 0 }] } })
            That(!DungeonEntryPlan.TryCreate(childDefinition,invalid,17,100,0,out _,out _,quest: parent,quests: sequentialCatalog),
                "顺序完成、父scalar或接取身份不一致时不借用子路线");
        var wrongChild = sequentialChild with { MinimumLevel = 95 };
        var wrongCatalog = new DfoServer.GameWorld.Quests.QuestCatalog(new() { [100] = parent, [101] = sequentialCatalog.Get(101), [102] = wrongChild });
        That(!DungeonEntryPlan.TryCreate(childDefinition,sequence,17,100,0,out _,out _,quest: parent,quests: wrongCatalog) &&
            !DungeonEntryPlan.TryCreate(childDefinition with { Mazes = [childMaze with { QuestConnectionTokens = [new(0,0),new(0,102),new(0,1)] }] },
                sequence,17,100,0,out _,out _,quest: parent,quests: sequentialCatalog),
            "当前子任务等级和迷宫难度不符仍拒绝，不以父请求绕过资格");
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
