using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer;

internal static class DungeonEntryPlanTests
{
    internal static void RunCore()
    {
        MooncreekDifficulty();
        var start = new DungeonMap("map", new(0, 0), 1001, "map/start.map");
        var boss = new DungeonMap("boss", new(1, 0), 1002, "map/boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB EE", [start.Position], [boss.Position], [start, boss],
            [new(0, 0), new(0, 100), new(0, -1)]);
        DungeonRawSection[] sections = [new("story mode", []), new("difficulty size", [new(0, 1)]),
            new("/story mode", []), new("difficulty level", [new(0, 1), new(0, 1), new(0, 1), new(0, 1), new(0, 0)])];
        var definition = new DungeonDefinition(17, "dungeon/quest.dgn", 1, 20, [maze], sections);
        var accepted = new ActiveMainQuest(0, 100, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Difficulty", 0, new(38, 1, 1, 1), null)
            { Level = 95, ActiveMainQuests = [accepted] };
        foreach (string grade in new[] { "[epic]", "[side]" })
        {
            var quest = QuestEligibilityTests.Quest(100) with { Grade = grade, Condition = "[clear map]", SubType = -1,
                IntData = [1002], DungeonIndex = 17 };
            for (byte difficulty = 0; difficulty <= 5; difficulty++)
            {
                bool ready = DungeonEntryPlan.TryCreate(definition, character, 17, 100, difficulty, out var plan, out var reason, quest: quest);
                That(ready == (difficulty < 4) && (ready
                    ? plan!.Difficulty == difficulty && plan.Maze == maze && plan.BossMap == boss && plan.QuestAcceptanceId == accepted.AcceptanceId
                    : reason == "difficulty-unsupported"), "主线与支线沿原连接接受开放难度，地下城难度表和协议上限仍拒绝越界");
            }
            foreach (var blocked in new[]
            {
                definition with { RawSections = [.. sections, new("designate dungeon difficulty", [new(0, 0)])] },
                definition with { DungeonType = "tower of death" },
                definition with { RawSections = sections.Where(row => row.Tag != "/story mode").ToArray() },
                definition with { RawSections = sections.Select(row => row.Tag == "difficulty size" ? row with { Tokens = [new(7, 1)] } : row).ToArray() }
            })
                That(!DungeonEntryPlan.TryCreate(blocked, character, 17, 100, 1, out _, out var reason, quest: quest) &&
                    reason == "difficulty-unsupported", "指定难度、特殊类型与非法故事块不能借普通任务难度扩展放行");
            That(!DungeonEntryPlan.TryCreate(definition, character with { CompletedQuests = [100], ActiveMainQuests = [] },
                17, 100, 1, out _, out _, quest: quest, isReplay: true), "回放继续限定普通难度");
        }
        var ordinary = definition with { Mazes = [maze with { QuestConnectionTokens = [] }] };
        That(DungeonEntryPlan.TryCreate(ordinary, character with { ActiveMainQuests = [] }, 17, 0, 0, out var normal, out _) &&
            normal is { IsOrdinary: true, Difficulty: 0, RouteQuestId: 0 }, "无任务普通入场不变");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze with { QuestConnectionTokens = [new(0, 0), new(0, 100), new(0, 2)] }] },
            character, 17, 100, 1, out _, out var connectionReason) && connectionReason == "quest-maze-unavailable",
            "非-1连接第三字段保留精确难度匹配，不擅自解释成最低难度");
        foreach (var quest in new[]
        {
            QuestEligibilityTests.Quest(100) with { Condition = "[seeking]", IntData = [9002, 1] },
            QuestEligibilityTests.Quest(100) with { Condition = "[seek n meet npc]", IntData = [9002, 1, 45] },
            QuestEligibilityTests.Quest(100) with { Condition = "[hunt monster]", IntData = [99, 3, 1, 1] }
        })
        {
            That(DungeonEntryPlan.TryCreate(ordinary, character, 17, 100, 0, out var borrowed, out _, quest: quest) &&
                borrowed is { IsOrdinary: true, RouteQuestId: 0 } && borrowed.QuestAcceptanceId == accepted.AcceptanceId,
                "收集/猎杀不要求dungeon info、目标图或目标难度匹配才能进入普通迷宫");
            var connected = maze with { QuestConnectionTokens = [new(0, 0), new(0, 100), new(0, 2)] };
            That(!DungeonEntryPlan.TryCreate(ordinary with { Mazes = [ordinary.Mazes[0], connected] },
                character, 17, 100, 0, out _, out _, quest: quest with { Grade = "[side]" }),
                "本任务明确配置特殊路线时不以普通迷宫绕过连接难度");
            That(DungeonEntryPlan.TryCreate(ordinary with { Mazes = [ordinary.Mazes[0], connected] },
                character, 17, 100, 0, out var epic, out _, quest: quest) && epic is { Difficulty: 2, RequestedDifficulty: 0, RouteQuestId: 100 } &&
                epic.Maze.QuestConnectionTokens.SequenceEqual(connected.QuestConnectionTokens),
                "主线自动提升至实际任务连接难度，仍进入专用路线而非普通借路");
        }
    }

    static void MooncreekDifficulty()
    {
        foreach (int id in new[] { 3200,3201,3202 })
        {
            var definition = new DungeonDefinition(id,"dungeon/event/dragon_kurun_kor/sample.dgn",84,87,[],
                [new("difficulty",[new(0,1)])]);
            That(Enumerable.Range(0,6).All(value => definition.AllowsDifficulty((byte)value,false) == (value == 1)),
                "月轮山三图仅接受资源固定难度1，不扩大到普通0或其它难度");
            foreach (var sections in new DungeonRawSection[][]
            {
                [], [new("difficulty",[])], [new("difficulty",[new(7,1)])], [new("difficulty",[new(0,2)])],
                [.. definition.RawSections,new("difficulty",[new(0,1)])],
                [.. definition.RawSections,new("designate dungeon difficulty",[new(0,0)])]
            })
                That(!Enumerable.Range(0,6).Any(value => (definition with { RawSections=sections }).AllowsDifficulty((byte)value,false)),
                    "月轮山缺失、畸形、重复或冲突的难度声明不能借特殊规则放行");
            That(!(definition with { Id=3203 }).AllowsDifficulty(1,false) &&
                !(definition with { Path="dungeon/other/sample.dgn" }).AllowsDifficulty(1,false),
                "其它编号和资源路径不沿用月轮山固定难度规则");
        }
    }

    static void QuestDifficultyRoutes(DungeonCatalog catalog, QuestCatalog quests)
    {
        var failures = new List<string>();
        foreach (var sample in new[] { (Dungeon: 101, Quest: 3708), (Dungeon: 104, Quest: 3749), (Dungeon: 86, Quest: 3571) })
        {
            var quest = quests.Get(sample.Quest);
            var accepted = new ActiveMainQuest(0, checked((ushort)sample.Quest),
                QuestClearEligibility.IsSingleMap(quest) ? 1 : QuestHuntProgress.Initial(quest))
                { AcceptanceId = Guid.NewGuid() };
            var character = new Character(1, 0, "Difficulty", 0, new(38, 1, 1, 1), null)
                { Level = 95, ActiveMainQuests = [accepted] };
            var source = catalog.Get(sample.Dungeon).Mazes.Single(row => row.ConnectsQuest((uint)sample.Quest));
            foreach (uint requested in new uint[] { (uint)sample.Quest, 0 })
            foreach (byte difficulty in new byte[] { 0, 1, 2, 3 })
            {
                bool ready = DungeonEntryPlan.TryCreate(catalog, character, (uint)sample.Dungeon, requested, difficulty,
                    out var plan, out var reason, quest: requested == 0 ? null : quest, quests: quests);
                if (!ready) failures.Add($"{sample.Dungeon}/{sample.Quest}/{requested}/{difficulty}: {reason}");
                else That(plan!.Difficulty == difficulty && plan.RouteQuestId == sample.Quest &&
                    plan.Maze.SourceOrdinal == source.SourceOrdinal && plan.Maze.SameLayout(source) &&
                    plan.QuestAcceptances.Single() == accepted,
                    "任务难度变化保留原任务连接、完整房间布局与接取身份");
            }
        }
        That(failures.Count == 0, "冒险任务地下城沿原路线接受自身已开放难度：" + string.Join("; ", failures));
    }

    static void SeaTrainParentClearMap(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var parent = quests.Get(3578);
        That(parent.IntData.SequenceEqual([3607,3608]) && quests.Get(3607).IntData.SequenceEqual([57858]),
            "破坏武器真实父子顺序与首房清图目标");
        foreach (byte job in new byte[] { 0,6,15 })
        foreach (bool acceptedChild in new[] { false,true })
        foreach (uint request in new uint[] { 3578,0 })
        {
            var active = new ActiveMainQuest(0,3578,2) { AcceptanceId = Guid.NewGuid() };
            var character = basis with { Job = job, GrowType = job == 15 ? (byte)20 : job == 6 ? (byte)18 : (byte)0,
                Level = 73, CompletedQuests = [3577], ActiveMainQuests = acceptedChild
                    ? [active,new(1,3607,1) { AcceptanceId = Guid.NewGuid() }] : [active] };
            That(DungeonEntryPlan.TryCreate(catalog,character,87,request,0,out var plan,out var reason,
                    quest: request == 0 ? null : parent,quests: quests) &&
                plan is { RouteQuestId: 3578, HasExplicitClearMap: true, StartMap.Id: 57858 } &&
                plan.QuestClearMap == plan.StartMap && plan.BossMap != plan.StartMap &&
                plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests),
                "父连接及普通0均借当前子目标，保留原Boss和真实接取；覆盖死灵6/18及日志15/20：" + reason);
        }
    }

    static void CompletedSeekingRoutes(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var quest = quests.Get(3735);
        var active = new ActiveMainQuest(0,3735,0) { AcceptanceId = Guid.NewGuid() };
        var material = new InventoryItem(1,new(InventoryListType.Main,177),new(10164801,ItemKind.Quest,15));
        var ready = basis with { Level = 95, ActiveMainQuests = [active], Inventory = new([material]) };
        foreach (uint request in new uint[] { 0,3735 })
        {
            for (byte difficulty = 0; difficulty <= 4; difficulty++)
                That(DungeonEntryPlan.TryCreate(catalog,ready,102,request,difficulty,out var plan,out var reason,
                        quest: request == 0 ? null : quest,quests: quests,draw: _ => 0) &&
                    plan is { RouteQuestId: 0, IsOrdinary: true, UsesFatigue: true } && plan.QuestId == request &&
                    plan.Maze.QuestConnectionTokens.Count == 0 && plan.StartMap.Id == 42151 &&
                    plan.QuestAcceptances.Single() == active &&
                    plan.QuestAcceptanceId == (request == 0 ? Guid.Empty : active.AcceptanceId),
                    "3735达成未交的显式与普通入口使用真实普通路线，保留难度、费用和接取身份：" + reason);
            foreach (var incomplete in new[]
            {
                ready with { ActiveMainQuests = [active with { ConditionState = 1 }] },
                ready with { Inventory = new([material with { Core = material.Core with { Value = 14 } }]) },
                ready with { Inventory = new([material with { Core = material.Core with { ExpireTime = 1 } }]) },
                ready with { ActiveMainQuests = [active with { AcceptanceId = Guid.Empty }] }
            })
            {
                That(DungeonEntryPlan.TryCreate(catalog,incomplete,102,request,0,out var story,out _,
                        quest: request == 0 ? null : quest,quests: quests) && story is { RouteQuestId: 3735, IsOrdinary: false } &&
                    story.StartMap.Id == 76510,"未达成、材料不足或过期、无接取身份均不能凭裸state0改普通路线");
                That(DungeonEntryPlan.TryCreate(catalog,incomplete,102,request,3,out var harder,out _,
                        quest: request == 0 ? null : quest,quests: quests) && harder is { RouteQuestId: 3735, Difficulty: 3, StartMap.Id: 76510 },
                    "未完成收集在开放难度使用原任务路线，不因难度绕过材料检查");
            }
        }
        That(ready.ActiveMainQuests.Single() == active && ready.CompletedQuests.Length == 0 &&
            ready.Inventory.Items.Single().Core.Value == 15,"路线规划不交任务、不消耗或奖励材料");
        var definition = catalog.Get(102);
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = definition.Mazes.Where(row => row.QuestConnectionTokens.Count != 0).ToArray() },
                ready,102,3735,0,out var taskOnly,out _,quest:quest,quests:quests) && taskOnly!.RouteQuestId == 3735,
            "没有真实普通maze时仍保留任务路线，不伪造房间");
    }

    static void Issue704Routes(PvfArchive pvf)
    {
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var quest = quests.Get(7917);
        That(quest.IntData.SequenceEqual([154,-1,-1,20]) && QuestHuntProgress.Initial(quest) == 20,
            "704真实暗精灵名誉市民绑定远古墓穴20只普通怪");
        var active = new ActiveMainQuest(0,7917,20) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1,0,"Mission",0,new(38,1,1,1),null)
            { Level = 95, CompletedQuests = [7916], ActiveMainQuests = [active] };
        That(DungeonEntryPlan.TryCreate(dungeons,character,154,7917,0,out var entry,out var reason,quest:quest,quests:quests) &&
            entry is { RouteQuestId: 7917 } && QuestHuntProgress.Bound(active,entry),
            "704使用PVF真实7917关联迷宫并保留接取身份：" + reason);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var monsters = entry!.Maze.MapSpecification!.SelectMany(map => DungeonMonsterPlan.FromRoom(
            rooms.Get(map,requirePassiveDefinitions:false),entry.CharacterId,1,entry.BasisLevel,playerTeam:entry.PlayerTeam).Monsters).ToArray();
        var progress = active;
        foreach (var monster in monsters)
            progress = QuestHuntProgress.MonsterDeath([progress],entry,quests,monster).Single();
        That(progress.ConditionState == 0 && progress.AcceptanceId == active.AcceptanceId,
            "7917实际四房资源提供足够普通怪物完成20只目标，不用Boss或客户端33补数");
        var recommended = quests.Get(8690);
        That(QuestDungeonClearProgress.Supports(recommended) && QuestDungeonClearProgress.Initial(recommended) == 10,
            "704真实推荐地下城任务十次初值");
        That(QuestAutoDailyPlan.Supports(quests.Get(8691)) && QuestCycle.Daily(quests.Get(8691)),
            "后继按自动每日分配，而不是普通一次性手工接取");
    }

    static void ClearedStoryUsesOrdinaryRoute(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var quest = quests.Get(3344);
        var dungeon = catalog.Get(51);
        That(QuestClearEligibility.IsSingleMap(quest) && quest.DungeonIndex == 51 && quest.IntData[0] == 76329 &&
            dungeon.Mazes.Any(row => row.QuestConnectionTokens.Count == 0) &&
            dungeon.Mazes.Any(row => row.HasActiveQuestConnection(3344)) &&
            Enumerable.Range(0,5).All(value => dungeon.AllowsDifficulty((byte)value,false) == (value < 3)) &&
            !dungeon.AllowsDifficulty(1,true,3344),
            "3344是哈穆林单图清图；循环图开放冒险和勇士，剧情块只有普通");
        var active = new ActiveMainQuest(0,3344,0) { AcceptanceId = Guid.NewGuid() };
        var ready = basis with { Level = 95, ActiveMainQuests = [active], CompletedQuests = [3343] };
        foreach (uint request in new uint[] { 3344, 0 })
        {
            for (byte difficulty = 0; difficulty <= 2; difficulty++)
                That(DungeonEntryPlan.TryCreate(catalog,ready,51,request,difficulty,out var plan,out var reason,
                        allowClearedQuest: request == 3344,
                        allowClearedTerminalQuest: request == 0 ? row => row.QuestId == 3344 && row.AcceptanceId == active.AcceptanceId : null,
                        quest: request == 0 ? null : quest,quests: quests,draw: _ => 0) &&
                    plan is { RouteQuestId: 0, IsOrdinary: true } && plan.QuestId == request &&
                    plan.Maze.QuestConnectionTokens.Count == 0 &&
                    plan.QuestAcceptanceId == (request == 0 ? Guid.Empty : active.AcceptanceId),
                    "3344达成未交使用循环普通迷宫：" + difficulty + "/" + reason + "/" + plan?.StartMap.Id + "/" + plan?.BossMap.Id);
            foreach (byte blocked in new byte[] { 3, 4 })
                That(!DungeonEntryPlan.TryCreate(catalog,ready,51,request,blocked,out _,out var blockedReason,
                        allowClearedQuest: request == 3344,
                        allowClearedTerminalQuest: request == 0 ? _ => true : null,
                        quest: request == 0 ? null : quest,quests: quests,draw: _ => 0) &&
                    blockedReason == "difficulty-unsupported",
                    "循环图不开放难度表以外的难度：" + blocked + "/" + blockedReason);
        }
        var pending = ready with { ActiveMainQuests = [active with { ConditionState = 1 }] };
        That(DungeonEntryPlan.TryCreate(catalog,pending,51,3344,0,out var story,out var storyReason,quest: quest,quests: quests) &&
            story is { RouteQuestId: 3344, IsOrdinary: false, Maze.ConnectionQuestId: 3344 } && story.BossMap.Id == 76329,
            "未达成仍走剧情迷宫：" + storyReason + "/" + story?.BossMap.Id);
        That(DungeonEntryPlan.TryCreate(catalog,pending,51,3344,1,out var harderStory,out _,quest: quest,quests: quests) &&
            harderStory is { RouteQuestId: 3344, Difficulty: 1, BossMap.Id: 76329 },"未达成任务在冒险难度保留原剧情终点");
        That(!DungeonEntryPlan.TryCreate(catalog,ready,51,3344,0,out _,out var bare,quest: quest,quests: quests) &&
            bare == "quest-condition-not-pending","没有清图凭据的条件0不能进入");
        That(DungeonEntryPlan.TryCreate(catalog,ready,51,0,0,out var locked,out _,quests: quests) &&
            locked is { RouteQuestId: 3344, IsOrdinary: false },
            "普通0在没有清图凭据时仍被未交付剧情锁住");
    }

    static void ClearedSequentialChildUsesOrdinaryRoute(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var clear = quests.Get(3560);
        var clearActive = new ActiveMainQuest(1,3560,0) { AcceptanceId = Guid.NewGuid() };
        var cleared = basis with { Level = 95, CompletedQuests = [3525], ActiveMainQuests = [clearActive] };
        That(clear.MainQuest == 3525 && QuestClearEligibility.IsSingleMap(clear), "3560是根特南门的顺序清图子任务");
        That(DungeonEntryPlan.TryCreate(catalog,cleared,82,3560,1,out var cycle,out var cycleReason,
                allowClearedQuest: true,quest: clear,quests: quests,draw: _ => 0) &&
            cycle is { RouteQuestId: 0, IsOrdinary: true, QuestId: 3560 } && cycle.Maze.QuestConnectionTokens.Count == 0,
            "顺序清图子任务达成未交后进入循环图冒险难度：" + cycleReason);
        var pendingClear = cleared with { ActiveMainQuests = [clearActive with { ConditionState = 1 }] };
        That(DungeonEntryPlan.TryCreate(catalog,pendingClear,82,3560,1,out var pendingPlan,out _,quest: clear,quests: quests) &&
            pendingPlan is { RouteQuestId: 3560, Difficulty: 1, Maze.ConnectionQuestId: 3560 },"未达成的顺序清图子任务在冒险难度保留自身连接路线");

        var hunt = quests.Get(3472);
        var huntActive = new ActiveMainQuest(2,3472,0) { AcceptanceId = Guid.NewGuid() };
        var hunted = basis with { Level = 95, CompletedQuests = [3457], ActiveMainQuests = [huntActive] };
        That(hunt.MainQuest == 3458 && hunt.Condition == "[hunt monster]", "3472是腐烂之地的顺序猎杀子任务");
        That(DungeonEntryPlan.TryCreate(catalog,hunted,158,3472,1,out var huntCycle,out var huntReason,
                quest: hunt,quests: quests,draw: _ => 0) &&
            huntCycle is { RouteQuestId: 0, IsOrdinary: true, QuestId: 3472 } && huntCycle.Maze.QuestConnectionTokens.Count == 0,
            "顺序猎杀子任务计数归零后进入循环图冒险难度：" + huntReason);
        uint pendingScalar = QuestHuntProgress.Initial(hunt);
        var pendingHunt = hunted with { ActiveMainQuests = [huntActive with { ConditionState = pendingScalar }] };
        That(pendingScalar != 0 && DungeonEntryPlan.TryCreate(catalog,pendingHunt,158,3472,1,out var huntPending,out _,quest: hunt,quests: quests) &&
            huntPending is { RouteQuestId: 3472, Difficulty: 1, Maze.ConnectionQuestId: 3472 },"猎杀尚未归零时在冒险难度保留原子任务路线");
    }

    static void DeathTowerCompanionPolicy(PvfArchive pvf, DungeonCatalog catalog)
    {
        var paths = DungeonCatalog.ParseDirectory(pvf.ReadTokens("dungeon/dungeon.lst"), pvf.ResolveString, "dungeon");
        That(paths.TryGetValue(11000, out var path) && path == "dungeon/towers/towerofdeath.dgn",
            "Death tower 11000 retains its real resource registration");
        string text = pvf.ReadText(path!);
        string type = PvfScript.Section(text, "dungeon type").Trim().Trim('`');
        int[] floors = PvfScript.Integers(PvfScript.Block(text, "death tower map indexes"));
        That(type == "tower of death" && !pvf.ReadTokens(path!).Any(token =>
                token.Type == 3 && pvf.ResolveString(token.Value) == "[maze info]"),
            "Real death tower declares its special type without an ordinary maze");
        That(floors.Length == 91 && floors[0] == 45 && Enumerable.Range(1, 45).All(floor =>
                floors[2 * floor - 1] == floor && floors[2 * floor] == 30000 + floor),
            "Real death tower preserves all 45 floor/map pairs");

        var ordinary = new DungeonDefinition(11000, "dungeon/ordinary.dgn", null, null, [], []);
        That(ordinary.AllowsCompanionAi && !(ordinary with { DungeonType = type }).AllowsCompanionAi &&
            !(ordinary with { Path = path! }).AllowsCompanionAi,
            "Death tower type and tower resource path independently block companions");
        bool unsupported = false;
        try { catalog.Get(11000); }
        catch (NotSupportedException error)
        {
            unsupported = error.Message == "unsupported: 未发现 maze info 资源结构";
        }
        That(unsupported, "Ordinary dungeon parser explicitly rejects the real maze-less death tower");
    }

    public static void Run(PvfArchive pvf)
    {
        QuestDifficultyRoutes(global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf),
            global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf));
        int checks = Count;
        GriefTowerResourceTests.Run(pvf);
        Issue704Routes(pvf);
        SpecialNormalDungeonEntryTests.Run(pvf);
        DungeonStoryFlowTests.RunStoryResources(pvf);

        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var soul = catalog.Get(337);
        That(soul.Path == "dungeon/boss_rush/boss_rush.dgn" && soul.MinimumLevel == 95 && soul.BasisLevel == 130 &&
            soul.IsBossRushDungeon && soul.TryGetDesignatedDifficulty(out byte soulDifficulty) && soulDifficulty == 0 &&
            soul.DisableFatigueConsumption && soul.EnterWithoutFatigue && !soul.DeclaresRequiredItem &&
            soul.RawSections.Any(row => row.Tag == "dungeon timeout" && row.Tokens is [{ Type:0,Value:120 },{ Type:0,Value:1 }]),
            "337确为95级、难度0、无资源门票及疲劳消耗的120秒BossRush，不能按缺票或疲劳不足诊断");
        var soulCharacter = new Character(99,0,"SoulEntry337",0,new(38,2,1085,249),null) { Level = 95 };
        That(DungeonEntryPlan.TryCreate(catalog,soulCharacter,337,0,0,out var soulPlan,out string soulReason) &&
            soulReason == "resource-plan-ready" && soulPlan is { UsesFatigue: false, DisableFatigueConsumption: true,
                IsBossRush: true, TimeoutMilliseconds: 120000 } &&
            soulPlan.StartMap.Position == new DungeonPoint(2,2) &&
            soulPlan.BossMap is { Position: { X: 4, Y: 4 }, Kind: "boss" } &&
            soulPlan.BossMap.Position.X < soulPlan.Maze.Width && soulPlan.BossMap.Position.Y < soulPlan.Maze.Height &&
            soulPlan.Maze.MapSpecification is { Count: 25 } && soulPlan.Maze.MapSpecification.Contains(soulPlan.BossMap) &&
            soulPlan.Maze.Bosses.SequenceEqual([new DungeonPoint(6,6)]) &&
            soul.Mazes.Single().Bosses.SequenceEqual([new DungeonPoint(6,6)]) &&
            soulPlan.Maze.IsBossRush && soulPlan.Maze.StaticTopology.Supported,
            "真实337不耗疲劳，按已有最远格规划结算房且不改共享目录的6,6声明；拓扑只核起点，并冻结 120 秒 BossRush 合同");
        // GM伴侣只在普通副本注入：祭坛/塔/亡者峡谷按资源标记整类排除，普通副本与Boss Rush保持放行。
        // 7601 眩惑之塔、11006/11007 两座祭坛、11008 绝望之塔首层。
        DeathTowerCompanionPolicy(pvf, catalog);
        That(new[] { 7601, 11006, 11007, 11008 }.All(id => !catalog.Get(id).AllowsCompanionAi) &&
            catalog.Get(337).AllowsCompanionAi && catalog.Get(3).AllowsCompanionAi && catalog.Get(5097).AllowsCompanionAi,
            "祭坛、塔与亡者峡谷不注入伴侣AIC，普通副本与Boss Rush保持放行");
        var ekern = new Character(17, 0, "Ekern", 1, new Location(35, 1, 0, 0, 0), null) { Level = 90 };
        var noQuests = new QuestCatalog(new());
        var cosmoCharacter = new Character(18, 0, "CosmoMode", 0, new Location(54, 2, 0, 0), null) { Level = 95 };
        That(!DungeonEntryPlan.TryCreate(catalog, cosmoCharacter, 7191, 0, 4, out _, out var cosmoModeReason,
                quests: noQuests, draw: _ => 0) && cosmoModeReason == "cosmo-mode-required",
            "超星空不能省略深渊模式退化成免邀请函、免灵石的普通副本：" + cosmoModeReason);
        That(!DungeonEntryPlan.TryCreate(catalog, cosmoCharacter, 7191, 0, 4, out _, out _,
                quests: noQuests, draw: _ => 0, hellParty: true, hellLegend: true),
            "超星空不能冒充每日星空目标模式");
        var cosmoDefinition = catalog.Get(7191);
        foreach (var malformed in new[]
        {
            cosmoDefinition with { RawSections = cosmoDefinition.RawSections.Where(row => row.Tag != "cosmo dungeon").ToArray() },
            cosmoDefinition with { RawSections = [.. cosmoDefinition.RawSections, new("cosmo dungeon", [])] },
            cosmoDefinition with { Id = 7192 }
        })
            That(!DungeonEntryPlan.TryCreate(malformed, cosmoCharacter, (uint)malformed.Id, 0, 4, out _, out var malformedReason,
                    quests: noQuests, draw: _ => 0, hellParty: true) && malformedReason == "cosmo-entry-contract-unsupported",
                "缺失、重复模式声明及未经支持的超星空编号不借普通布局放行：" + malformedReason);
        foreach (uint ekernDungeon in new uint[] { 5097, 5098 })
        {
            That(DungeonEntryPlan.TryCreate(catalog, ekern, ekernDungeon, 0, 2, out var opened, out var openedReason, draw: _ => 0, quests: noQuests) &&
                opened is { Difficulty: 2, IsOrdinary: true, DisableFatigueConsumption: true, QuestId: 0 } &&
                opened.Maze.StaticTopology.Supported && openedReason == "resource-plan-ready",
                "魔界集结地等待区的艾肯副本按难度 2 进入：" + ekernDungeon + " " + openedReason);
            int mazes = catalog.Get(checked((int)ekernDungeon)).Mazes.Count;
            if (mazes > 1)
                That(DungeonEntryPlan.TryCreate(catalog, ekern, ekernDungeon, 0, 2, out var second, out _, draw: _ => 1, quests: noQuests) &&
                    second!.Maze.SourceOrdinal == 1, "5097 第二条迷宫同样可进入");
            That(!DungeonEntryPlan.TryCreate(catalog, ekern, ekernDungeon, 0, 0, out _, out var wrongDifficulty, quests: noQuests) &&
                wrongDifficulty == "difficulty-unsupported", "艾肯副本不接受难度 0：" + ekernDungeon);
        }
        That(!DungeonEntryPlan.TryCreate(catalog, ekern with { Level = 89 }, 5097, 0, 2, out _, out var tooLow, quests: noQuests) &&
            tooLow == "dungeon-level-too-low", "89 级不能进入遗落的神殿");
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
        That(!definition.DisableGiveupPenalty && !selected.DisableGiveupPenalty &&
            DungeonEntryPlan.TryCreate(definition with { DisableGiveupPenalty = true },character,3,3145,0,out var exemptPlan,out _) &&
            exemptPlan is { DisableGiveupPenalty: true } && !selected.DisableGiveupPenalty && !definition.DisableGiveupPenalty,
            "入场冻结本次资源退图豁免，保留共享定义与此前实例的原资格");
        That(DungeonEntryPlan.TryCreate(definition with { DisableFatigueConsumption = true },
            character with { ActiveMainQuests = [] },3,0,0,out var noFatiguePlan,out _) && noFatiguePlan is
            { IsOrdinary: true, RequiresFatigueOwner: true, UsesFatigue: false, DisableFatigueConsumption: true,
              DisableGiveupPenalty: false, HasFreeCombatConsumption: false } && !definition.DisableFatigueConsumption,
            "no fatigue只冻结疲劳值免扣，不绕过原owner校验、不免虚弱或道具消耗，也不改共享定义");
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        AuxiliaryEquipmentHuntRoutes(catalog,quests,character);
        OrdinaryHuntRoutes(catalog,quests,rooms,character);
        AwakeningDungeonRoutes(catalog,quests,rooms,character);
        TournamentEntry(catalog,character,rooms);
        HarlemSeekingRoutes(catalog,quests,character);
        SeaTrainParentClearMap(catalog,quests,character);
        CompletedSeekingRoutes(catalog,quests,character);
        ClearedStoryUsesOrdinaryRoute(catalog,quests,character);
        ClearedSequentialChildUsesOrdinaryRoute(catalog,quests,character);
        var passiveQuest = quests.Get(3205);
        var passiveCharacter = character with { Level = 95, CompletedQuests = [3204],
            ActiveMainQuests = [new(0,3205,0) { AcceptanceId = Guid.NewGuid() }] };
        That(QuestHuntProgress.TryTargets(passiveQuest,out var passiveTargets) && passiveTargets.Any(target => target.EnemyType == 3),
            "3205真实目标含被动物件，条件0不能当成普通怪物猎杀完成");
        foreach (uint request in new uint[] { 3205, 0 })
            That(DungeonEntryPlan.TryCreate(catalog,passiveCharacter,25,request,0,out var passivePlan,out var passiveReason,
                    quest: request == 0 ? null : passiveQuest,quests: quests) &&
                passivePlan is { RouteQuestId: 3205, IsOrdinary: false, BossMap.Id: 53493 } &&
                passivePlan.QuestAcceptances.Single() == passiveCharacter.ActiveMainQuests.Single(),
                "被动物件裸条件0仍冻结原接取和任务终点，显式与普通入口一致：" + passiveReason);
        GentSouthGateQuestLevel(catalog,quests,character);
        NonoraGroznyEntry(catalog,quests,rooms,character,pvf);
        RecoverAt5tWalkerEntry(catalog,quests,rooms,character);
        foreach (var scenario in new[] { (Quest:3560,Dungeon:82,Level:(byte)65,Target:92247,Boss:92251),
            (Quest:3753,Dungeon:104,Level:(byte)83,Target:76648,Boss:76649) })
        {
            var targetQuest = quests.Get(scenario.Quest);
            var accepted = character with { Level = scenario.Level,
                ActiveMainQuests = [new(5,checked((ushort)scenario.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,accepted,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out var middlePlan,out var middleReason,quest: targetQuest,quests: quests) &&
                middlePlan is { QuestClearMap: not null } && middlePlan.QuestClearMap.Id == scenario.Target &&
                middlePlan.BossMap.Id == scenario.Boss && middlePlan.BaseBossMap == middlePlan.BossMap &&
                !middlePlan.IsTerminalMap(middlePlan.QuestClearMap),
                "中途目标独立于真实Boss，保留原迷宫而不投影任务终点：" + scenario.Quest + "/" + middleReason);
            var missingTarget = targetQuest with { IntData = [int.MaxValue] };
            That(!DungeonEntryPlan.TryCreate(catalog,accepted,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out _,out _,quest: missingTarget,quests: quests),"未声明的地图号不能借中途清图放行");
        }
        var eggQuest = quests.Get(6032);
        var eggAcceptance = new ActiveMainQuest(5,6032,1) { AcceptanceId = Guid.NewGuid() };
        var eggCharacter = character with { Level = 92, CompletedQuests = [6031], ActiveMainQuests = [eggAcceptance] };
        foreach (uint requestQuest in new uint[] { 6032,0 })
        {
            That(DungeonEntryPlan.TryCreate(catalog,eggCharacter,291100319,requestQuest,0,out var eggPlan,out var eggReason,
                quest: requestQuest == 0 ? null : eggQuest,quests: quests) && eggPlan is
                { BossMap.Id: 59914, BossMap.Position.X: 3, BossMap.Position.Y: 0,
                  TerminalLayerMap.Id: 59914, TerminalLayerMap.Position.X: 2, TerminalLayerMap.Position.Y: 0 } &&
                eggPlan.TerminalLayerAcceptanceId == eggAcceptance.AcceptanceId && eggPlan.TerminalLayerQuestId == eggAcceptance.QuestId &&
                eggPlan.TerminalLayerQuestSlot == eggAcceptance.Slot && !eggPlan.IsTerminalMap(eggPlan.BossMap),
                "被偷走的卵保留原Boss坐标并冻结原接取的实际末层，显式及普通0请求一致：" + eggReason);
            var frozenEgg = eggPlan ?? throw new InvalidOperationException("真实末层入场计划未建立");
            var information = DfoServer.Network.Builders.DungeonPackets.Information(frozenEgg);
            That(information[8] == 3 && information[9] == 0, "终末别名不改变NOTI28原grid");
            var actual = DungeonMonsterPlan.Create(rooms,frozenEgg.TerminalLayerMap!,eggCharacter.Id,100,
                frozenEgg.BasisLevel,frozenEgg.PlayerTeam);
            That(actual.Room.DeclaredType == "[boss]" && actual.Monsters.Any(monster => monster.MonsterId == 70215 &&
                monster.IsScriptedBoss && monster.IsBoss && monster.CanReportDeath),
                "真实末层保留MAP自身boss类型和完整dummy演员，不伪造剧情死亡");
        }
        var plazaQuest = quests.Get(3946);
        That(DungeonEntryPlan.TryCreate(catalog,character with { Level = 95 },310,0,0,out var plazaHell,out var plazaHellReason,
            hellParty: true,quests: quests) && plazaHell is { HellParty.MapId: 60083, HellParty.RequiredItemCount: 28 },
            "时间广场普通深渊使用真实密门和门票声明：" + plazaHellReason);
        var plazaCharacter = character with { Level = 88,CompletedQuests = [3945],
            ActiveMainQuests = [new(5,3946,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,plazaCharacter,310,3946,0,out var plazaPlan,out var plazaReason,
            quest: plazaQuest,quests: quests) && plazaPlan is { Maze.SourceOrdinal: 1,StartMap.Id: 76670,BossMap.Id: 76678,BaseBossMap.Kind: "boss" } &&
            plazaPlan.QuestAcceptanceId == plazaCharacter.ActiveMainQuests.Single().AcceptanceId && plazaPlan.Maze.StaticTopology.Supported,
            "真实杂乱的开始在魔兽区块之后仍可解析并选择原九图任务路线：" + plazaReason);
        That(DungeonEntryPlan.TryCreate(catalog,plazaCharacter,310,0,0,out var automaticPlaza,out _,quests: quests) &&
            automaticPlaza is { QuestId: 0,RouteQuestId: 3946,Maze.SourceOrdinal: 1,BossMap.Id: 76678,QuestAcceptanceId: var plazaAcceptance } &&
            plazaAcceptance == Guid.Empty && automaticPlaza.QuestAcceptances.SequenceEqual(plazaCharacter.ActiveMainQuests),
            "时间广场普通零请求复用当前3946路线，不借用魔兽布局或伪造请求接取身份");
        foreach (var scenario in new[] { (Quest:3772,Dungeon:235,Previous:3771), (Quest:3773,Dungeon:227,Previous:3772),
            (Quest:3774,Dungeon:1005,Previous:3773),
            (Quest:3776,Dungeon:1007,Previous:3775), (Quest:3777,Dungeon:238,Previous:3776) })
        {
            var target = quests.Get(scenario.Quest);
            var pending = character with { Level = 84, CompletedQuests = [scenario.Previous],
                ActiveMainQuests = [new(5,checked((ushort)scenario.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            var resource = catalog.Get(scenario.Dungeon);
            var source = resource.Mazes.Single(maze => maze.ConnectsQuest(checked((uint)scenario.Quest)));
            var sourceMaps = source.MapSpecification ?? throw new InvalidDataException("任务路线缺少显式地图：" + scenario.Quest);
            var originalBoss = sourceMaps.Single(map => map.Position == source.Bosses.Single());
            That(originalBoss.Kind == "map" && originalBoss.Id == target.IntData[0],
                "真实任务Boss坐标的基础map行与清图目标一致：" + scenario.Quest);
            That(DungeonEntryPlan.TryCreate(catalog,pending,checked((uint)scenario.Dungeon),checked((uint)scenario.Quest),0,
                out var declared,out var declaredReason,quest: target,quests: quests),
                "真实任务沿声明的Boss坐标进入原路线：" + scenario.Quest + "/" + declaredReason);
            That(declared!.BaseBossMap == (originalBoss with { Kind = "boss" }) && declared.BossMap == declared.BaseBossMap &&
                declared.QuestAcceptanceId == pending.ActiveMainQuests.Single().AcceptanceId &&
                declared.QuestAcceptances.SequenceEqual(pending.ActiveMainQuests) && sourceMaps.Contains(originalBoss) &&
                sourceMaps.Single(map => map.Position == originalBoss.Position).Kind == "map" && declared.Maze.StaticTopology.Supported,
                "Boss投影保留原地图、接取及完整拓扑，不改共享资源：" + scenario.Quest);
            That(DungeonEntryPlan.TryCreate(catalog,pending,checked((uint)scenario.Dungeon),0,0,out var automaticDeclared,out _,quests: quests) &&
                automaticDeclared!.BossMap == declared.BossMap && automaticDeclared.RouteQuestId == scenario.Quest &&
                automaticDeclared.QuestId == 0 && automaticDeclared.QuestAcceptanceId == Guid.Empty,
                "普通选图的活动任务路线复用相同Boss投影：" + scenario.Quest);
            var planned = new List<DungeonMonsterPlan>();
            uint next = 100;
            foreach (var map in declared.Maze.MapSpecification!)
            {
                var room = DungeonMonsterPlan.Create(rooms,map,pending.Id,checked((ushort)next),declared.BasisLevel,declared.PlayerTeam);
                planned.Add(room);
                if (room.Monsters.Count > 0 || room.SpecialObjects.Count > 0) next = room.NextObjectKey;
            }
            That(planned.Count == declared.Maze.MapSpecification.Count && planned.All(room =>
                room.Room.Monsters.All(monster => room.Monsters.Any(actor => actor.Source == monster)) &&
                room.Room.AiCharacters.Where(actor => !DungeonCompanion.IsCompanionRow(actor.Id))
                    .All(actor => room.Monsters.Any(monster => monster.AiCharacter == actor)) &&
                room.Room.SpecialObjects.All(sourceObject => room.SpecialObjects.Any(actor => actor.Source == sourceObject))),
                "真实整条路线保留普通、dummy及友方APC演员：" + scenario.Quest);
            var route = new DungeonFixedRoute(planned,declared.Maze.StaticTopology.Links!
                .Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
            That(SinglePlayerDungeonRun.TryCreate(declared,1,100,route,new NativeDungeonCombatAuthority(),out _,out _),
                "Boss投影后的全部真实房间可建立原正式Run：" + scenario.Quest);
        }
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
        var heart = character with { Level = 84, CompletedQuests = [3777],
            ActiveMainQuests = [new(5,3778,1) { AcceptanceId = Guid.NewGuid() }] };
        var heartQuest = quests.Get(3778);
        var heartResource = catalog.Get(233);
        That(heartResource.TryGetDesignatedDifficulty(out var normalHeartDifficulty) && normalHeartDifficulty == 2 &&
            heartResource.AllowsDifficulty(0,true) && !heartResource.AllowsDifficulty(2,true),
            "真实安徒恩心脏保留普通指定二难度元数据，故事只接受自身零难度");
        That(DungeonEntryPlan.TryCreate(catalog,heart,233,3778,0,out var heartPlan,out var heartReason,quest: heartQuest,quests: quests) &&
            heartPlan is { Difficulty: 0, StartMap.Id: 310064, BossMap.Id: 310065, IsQuestReplay: false } &&
            heartPlan.QuestAcceptanceId == heart.ActiveMainQuests.Single().AcceptanceId && heartPlan.Maze.StaticTopology.Supported,
            "真实3778显式请求选择原故事路线及接取：" + heartReason);
        That(DungeonEntryPlan.TryCreate(catalog,heart,233,0,0,out var autoHeart,out _,quests: quests) &&
            autoHeart is { Difficulty: 0, RouteQuestId: 3778, StartMap.Id: 310064, BossMap.Id: 310065, QuestId: 0 },
            "普通0请求的活动3778路线同样使用故事难度");
        var replayHeart = new DfoServer.Game.Quests.QuestReplayPlan(heart with { ActiveMainQuests = [], CompletedQuests = [3777,3778] },heartQuest,233);
        That(DungeonEntryPlan.TryCreateReplay(catalog,replayHeart,out var heartReplay,out _) &&
            heartReplay is { IsQuestReplay: true, Difficulty: 0, QuestId: 0, RouteQuestId: 3778, BossMap.Id: 310065 } &&
            !DungeonEntryPlan.TryCreate(catalog,heart,233,3778,2,out _,out _,quest: heartQuest,quests: quests),
            "已完成3778回放保留故事零难度，活动任务不能借普通指定二难度进入");
        foreach (var sample in new[] { (Quest:6206,Previous:6205),(Quest:6214,Previous:6213) })
        {
            var storyQuest = quests.Get(sample.Quest);
            var pending = character with { Level = 95,CompletedQuests = [sample.Previous],
                ActiveMainQuests = [new(5,checked((ushort)sample.Quest),1) { AcceptanceId = Guid.NewGuid() }] };
            foreach (uint requested in new uint[] { checked((uint)sample.Quest),0 })
            {
                That(DungeonEntryPlan.TryCreate(catalog,pending,291100373,requested,0,out var storyPlan,out var storyReason,
                    quest: requested == 0 ? null : storyQuest,quests: quests) && storyPlan is
                    { Maze.ConnectionQuestId: var connection,StartMap.Id: 292106186,BossMap.Id: 292106190 } && connection == sample.Quest,
                    "真实多故事块显式及自动请求选择最终任务块：" + sample.Quest + "/" + storyReason);
                foreach (byte rejected in new byte[] { 1,2,3,4 })
                    That(!DungeonEntryPlan.TryCreate(catalog,pending,291100373,requested,rejected,out _,out var storyRejectedReason,
                        quest: requested == 0 ? null : storyQuest,quests: quests) && storyRejectedReason == "difficulty-unsupported",
                        "真实故事size1不借其他块或普通难度放行");
                var plans = new List<DungeonMonsterPlan>();
                ushort nextKey = 100;
                foreach (var map in storyPlan!.Maze.MapSpecification!)
                {
                    var actors = DungeonMonsterPlan.Create(rooms,map,pending.Id,nextKey,storyPlan.BasisLevel,storyPlan.PlayerTeam);
                    plans.Add(actors);
                    if (actors.Monsters.Count > 0 || actors.SpecialObjects.Count > 0) nextKey = checked((ushort)actors.NextObjectKey);
                }
                That(plans.Count == 5 && plans.Sum(room => room.Monsters.Count) > 0 && plans.All(room =>
                    room.Room.Monsters.All(source => room.Monsters.Any(actor => actor.Source == source))),
                    "真实五房保留全部源怪物，不用空房替代任务路线");
            }
        }
        PreyIsysSinQuestTests.RunResources(pvf, quests);
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
        var parentStory = catalog.Get(239) with { RawSections = StoryBlocks(5150,3273) };
        foreach (uint requested in new uint[] { 3273,0 })
        {
            That(DungeonEntryPlan.TryCreate(parentStory,noir,239,requested,0,out var connected,out var connectedReason,
                quest: requested == 0 ? null : quests.Get(3273),quests: quests) && connected is
                { Maze.ConnectionQuestId: 5150,RouteQuestId: 3273 },
                "多故事块使用实际子maze连接身份，保留父接取及自动请求身份：" + connectedReason);
            That(!DungeonEntryPlan.TryCreate(parentStory,noir,239,requested,1,out _,out var rejectedStory,
                quest: requested == 0 ? null : quests.Get(3273),quests: quests) && rejectedStory == "difficulty-unsupported",
                "父任务所属另一故事块的较高难度不能泄漏到实际子路线");
        }
        var replayStory = heartResource with { RawSections = StoryBlocks(3778,9999) };
        That(DungeonEntryPlan.TryCreate(replayStory,replayHeart.Expected,233,3778,0,out var storyReplay,out _,
            quest: heartQuest,isReplay: true) && storyReplay is { IsQuestReplay: true,Maze.ConnectionQuestId: 3778 },
            "回放同样按最终maze选择故事块");
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
        var alias = fixtureBoss with { Kind = "layered", Position = fixtureStart.Position };
        var aliasMaze = otherMaze with { LayeredMaps = [alias] };
        var aliasAccepted = new ActiveMainQuest(0,100,1) { AcceptanceId = Guid.NewGuid() };
        var aliasCharacter = otherCharacter with { ActiveMainQuests = [aliasAccepted] };
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },aliasCharacter,17,100,0,
            out var aliasEntry,out _,quest: unlinkedQuest) && aliasEntry!.TerminalLayerMap == alias &&
            aliasEntry.BossMap == fixtureBoss && aliasEntry.TerminalLayerAcceptanceId == aliasAccepted.AcceptanceId,
            "不同编号及坐标的单地图资源同样冻结末层，不依赖真实任务号");
        foreach (var invalid in new[]
        {
            aliasMaze with { LayeredMaps = [alias,alias with { Path = "map/second.map" }] },
            aliasMaze with { LayeredMaps = [alias with { Path = "map/other.map" }] },
            aliasMaze with { LayeredMaps = [alias,fixtureStart with { Kind = "layered" }] }
        })
            That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [invalid] },aliasCharacter,17,100,0,
                out _,out _,quest: unlinkedQuest), "歧义、异路径及非末层不能回退为基础Boss通关");
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },pendingClear,17,100,0,
            out _,out _,quest: unlinkedQuest), "末层别名缺少真实接取身份时拒绝");
        var clearedAlias = aliasCharacter with { ActiveMainQuests = [aliasAccepted with { ConditionState = 0 }] };
        That(!DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },clearedAlias,17,100,0,
            out _,out _,quest: unlinkedQuest) &&
            DungeonEntryPlan.TryCreate(unlinkedDefinition with { Mazes = [aliasMaze] },clearedAlias,17,100,0,
                out var repeatedAlias,out _,allowClearedQuest:true,quest: unlinkedQuest) &&
            repeatedAlias!.TerminalLayerMap == alias && repeatedAlias.TerminalLayerAcceptanceId == aliasAccepted.AcceptanceId,
            "裸state0拒绝；原入口已核对持久清图凭据的同接取重入保留实际末层");
        var baseMapMaze = otherMaze with { MapSpecification = [fixtureStart,fixtureBoss with { Kind = "map" }] };
        var baseMapDefinition = definition with { Id = 17, Mazes = [baseMapMaze] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition,pendingClear,17,100,0,out var baseMapPlan,out _,quest: unlinkedQuest) &&
            baseMapPlan!.BaseBossMap == fixtureBoss && baseMapMaze.MapSpecification.Single(map => map.Position == fixtureBoss.Position).Kind == "map",
            "不同编号的显式清图目标仅投影唯一Boss坐标，不改原始map声明");
        var sharedStartMaze = baseMapMaze with { Starts = [fixtureBoss.Position] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition with { Mazes = [sharedStartMaze] },pendingClear,17,100,0,
            out var sharedStart,out _,quest: unlinkedQuest) && sharedStart!.StartMap == sharedStart.BaseBossMap &&
            sharedStart.Maze.MapSpecification!.Contains(sharedStart.StartMap) &&
            sharedStartMaze.MapSpecification!.Single(map => map.Position == fixtureBoss.Position).Kind == "map",
            "起点与Boss共图时同步本次投影身份，正式Run可复用迷宫内的起点，原资源仍保持map");
        var conditionalBoss = fixtureStart with { Position = fixtureBoss.Position, IsConditional = true };
        var conditionalStart = fixtureBoss with { Position = fixtureStart.Position, Kind = "map", IsConditional = true };
        var conditionalMaze = baseMapMaze with { BossEntrance = new(0,0,[]),
            WarpMaps = [new(fixtureBoss.Position,fixtureStart.Position),new(fixtureStart.Position,fixtureBoss.Position)],
            ConditionalMaps = [conditionalBoss,conditionalStart] };
        That(DungeonEntryPlan.TryCreate(baseMapDefinition with { Mazes = [conditionalMaze] },pendingClear,17,100,0,
            out var conditionalPlan,out _,quest: unlinkedQuest) &&
            conditionalPlan!.Maze.ConditionalMaps.SequenceEqual([conditionalBoss with { Kind = "boss" },conditionalStart]) &&
            conditionalMaze.ConditionalMaps[0].Kind == "map",
            "条件传送仅同步Boss来源的投影种类，保留替换图身份、其他来源及共享资源");
        foreach (var invalid in new[] { unlinkedQuest with { IntData = [fixtureStart.Id] }, parent })
            That(!DungeonEntryPlan.TryCreate(baseMapDefinition,pendingClear,17,100,0,out _,out _,quest: invalid),
                "中途目标及父聚合条件不能将基础map升级为Boss");
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out var unlinked,out _,quest: unlinkedQuest) &&
            unlinked is { QuestId: 100, RouteQuestId: 100, BossMap.Id: 456 },
            "合成不同任务和地图同样复用明确声明的唯一无连接清图路线，不硬编码转职编号");
        That(DungeonEntryPlan.TryCreate(unlinkedDefinition,pendingClear,17,100,0,out var mapOnlyClear,out _,quest: unlinkedQuest) &&
            mapOnlyClear is { BossMap.Id: 456 },
            "仅声明MAP且目标确在无连接路线时允许清图任务借用该路线");
        foreach (var unsupported in new[] { unlinkedQuest with { Definition = unlinkedQuest.Definition! with { Dungeons = [new(18,-1)] } },
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
        DummyAndHellLayouts(catalog,global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf),character);
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

        static DungeonRawSection[] StoryBlocks(int firstQuest, int secondQuest) =>
        [
            new("story mode",[]),new("difficulty size",[new(0,1)]),new("quest list",[new(0,firstQuest)]),
            new("/quest list",[]),new("/story mode",[]),
            new("story mode",[]),new("difficulty size",[new(0,3)]),new("quest list",[new(0,secondQuest)]),
            new("/quest list",[]),new("/story mode",[])
        ];
    }

    static void DummyAndHellLayouts(DungeonCatalog catalog,DungeonRoomCatalog rooms,Character basis)
    {
        var character = basis with { Level = 95,ActiveMainQuests = [] };
        var definition = catalog.Get(51);
        var raw = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        That(definition.DummyAppearCount == 2 && raw.Bosses.Count == 3 && raw.MapAlternatives.Count == 3,
            "D51保留两个dummy声明、三个Boss候选及同格普通图，不误判为覆盖");
        foreach (bool explicitMaps in new[] { true,false })
        {
            var source = explicitMaps ? raw : raw with { MapSpecification = null,MapAlternatives = [] };
            var maze = catalog.ResolveOrdinaryMaze(definition,source,_ => 0);
            That(maze.StaticTopology.Supported && maze.Starts.Count == 1 && maze.Bosses.Count == 1 && maze.DummyStages.Count == 3 &&
                maze.DummyStages.Take(2).All(map => rooms.Get(map,requirePassiveDefinitions:false).DeclaredType == "[dummy]") &&
                rooms.Get(maze.DummyStages[^1],requirePassiveDefinitions:false) is { DeclaredType: "[boss]" } boss &&
                boss.Monsters.Any(monster => monster.MonsterId == 1077 && monster.RankKind == "[boss]"),
                "D51显式/反向地图池均生成两次假Boss及1077真Boss：" + explicitMaps);
            That(maze.DummyStages.Take(2).All(map => DungeonMonsterPlan.FromRoom(rooms.Get(map,requirePassiveDefinitions:false),
                character.Id,10,definition.BasisLevel,playerTeam:0).Monsters.Any(monster =>
                    monster.MonsterId == 1078 && monster.IsDummyMapBoss && monster.SpawnType == 3 && monster.RewardType == 1 && !monster.IsBoss)),
                "真实D51每个dummy阶段按Boss3下发1078并保留目标更新资格，不提前结算或改变奖励品级");
            That(ReferenceEquals(catalog.ResolveOrdinaryMaze(definition,maze,_ => throw new InvalidOperationException()),maze),
                "已冻结dummy路线不再次随机或改变阶段对象");
            That(DungeonEntryPlan.TryCreate(definition,character,51,0,0,out var plan,out var reason,resolveMaze:_ => maze) &&
                !plan!.IsTerminalMap(maze.DummyStages[0]) && plan.IsTerminalMap(maze.DummyStages[^1]),"D51准入与真实终点：" + reason);
        }
        foreach (int pick in new[] { 0,1 })
        {
            That(DungeonEntryPlan.TryCreate(catalog,character,291100319,0,4,out var plan,out var reason,
                draw:count => Math.Min(pick,count-1),hellParty:true) && plan is { HellParty: not null } &&
                plan.Maze.Starts.Count == 1 && plan.Maze.Bosses.Count == 1 && plan.Maze.StaticTopology.Supported &&
                plan.Maze.MapSpecification!.Single(map => map.Position == plan.HellParty.Room).Id == plan.HellParty.MapId,
                "真实深渊多组迷宫与起终点候选完成组格并替换密门：" + reason);
        }
    }

    static void AuxiliaryEquipmentHuntRoutes(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        var results = new List<string>();
        foreach (var sample in new[] { (Quest:674,Dungeon:163,Difficulty:(byte)2), (Quest:675,Dungeon:82,Difficulty:(byte)2),
            (Quest:675,Dungeon:82,Difficulty:(byte)3) })
        {
            var quest = quests.Get(sample.Quest);
            var active = new ActiveMainQuest(5,checked((ushort)sample.Quest),QuestHuntProgress.Initial(quest)) { AcceptanceId=Guid.NewGuid() };
            var character = basis with { Level=checked((byte)quest.MinimumLevel),ActiveMainQuests=[active] };
            bool created = DungeonEntryPlan.TryCreate(catalog,character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),
                sample.Difficulty,out var plan,out var reason,quest:quest,draw:_ => 0,quests:quests);
            if (!created || plan is not { IsOrdinaryHunt:true,IsOrdinary:true,UsesFatigue:true,RouteQuestId:0 } ||
                plan.QuestId != sample.Quest || plan.Difficulty != sample.Difficulty || plan.ActiveQuestSlot != active.Slot ||
                plan.QuestAcceptanceId != active.AcceptanceId || !plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests))
                results.Add($"{sample.Quest}/{sample.Dungeon}/{sample.Difficulty}: {reason}");
            if (!created || plan is null) continue;
            var after = active;
            That(QuestHuntProgress.TryTargets(quest,out var targets) && targets.Length == 3,"真实资格任务保留三个猎杀通道");
            foreach (var target in targets)
            {
                var monster = new DungeonPlannedMonster(0,1,target.MonsterId,1,null);
                after = QuestHuntProgress.MonsterDeath([after],plan,quests,monster).Single();
                That(QuestHuntProgress.MonsterDeath([active with { AcceptanceId=Guid.NewGuid() }],plan,quests,monster).Single().ConditionState ==
                    active.ConditionState,"真实入场计划的旧接取不能推进重接任务");
            }
            That(after.ConditionState == 0 && after.AcceptanceId == active.AcceptanceId,"真实入场计划绑定三个实际模板的权威死亡推进");
            foreach (byte difficulty in new byte[] { 0,1,4 }.Concat(sample.Dungeon == 163 ? new byte[] { 3 } : []).Distinct())
                That(DungeonEntryPlan.TryCreate(catalog,character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),difficulty,
                    out _,out _,quest:quest,quests:quests) == catalog.Get(sample.Dungeon).AllowsDifficulty(difficulty, false),
                    "资格任务不阻拦低难度入场，仍保留真实DUN难度上限："+sample.Quest+"/"+difficulty);
            That(DungeonEntryPlan.TryCreate(catalog,character with { Level=95 },sample.Dungeon == 163 ? 82u : 163u,checked((uint)sample.Quest),2,
                out var unrelated,out _,quest:quest,quests:quests) && targets.All(target =>
                    QuestHuntProgress.MonsterDeath([active],unrelated!,quests,new(0,1,target.MonsterId,1,null)).Single().ConditionState == active.ConditionState),
                "携带猎杀任务可进入其它普通副本，但错图击杀不推进目标");
            foreach (var invalid in new[] { character with { ActiveMainQuests=[] },character with { CompletedQuests=[sample.Quest] },
                character with { ActiveMainQuests=[active with { AcceptanceId=Guid.Empty }] },
                character with { ActiveMainQuests=[active,new(5,100,1) { AcceptanceId=Guid.NewGuid() }] } })
                That(!DungeonEntryPlan.TryCreate(catalog,invalid,checked((uint)sample.Dungeon),checked((uint)sample.Quest),sample.Difficulty,
                    out _,out _,quest:quest,quests:quests),"资格任务保留唯一活动接取门禁");
            var definition = catalog.Get(sample.Dungeon);
            var normal = definition.Mazes.First(maze => maze.QuestConnectionTokens.Count == 0);
            foreach (var connection in new[] { new PvfToken[] {new(0,0),new(0,sample.Quest),new(0,0)},
                [new(0,2),new(0,100),new(0,-1)] })
                That(DungeonEntryPlan.TryCreate(definition with { Mazes=[normal,normal with { QuestConnectionTokens=connection }] },
                    character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),sample.Difficulty,out _,out _,quest:quest) == (connection[0].Value == 2),
                    "自身特殊路线仍须匹配，其它未知连接不阻拦现有普通迷宫");
            That(!DungeonEntryPlan.TryCreate(definition with { Mazes=[] },character,checked((uint)sample.Dungeon),checked((uint)sample.Quest),
                sample.Difficulty,out _,out _,quest:quest),"资格任务仍要求真实普通迷宫");
        }
        That(results.Count == 0,"真实装备槽资格任务按最低难度进入普通路线并保留接取身份："+string.Join("; ",results));
        var probe = quests.Get(675);
        var pending = basis with { Level=65,ActiveMainQuests=[new(5,675,QuestHuntProgress.Initial(probe)) { AcceptanceId=Guid.NewGuid() }] };
        foreach (int parameter in new[] { -2,-1,0,1,2,3,4 })
            That(DungeonEntryPlan.TryCreate(catalog,pending,82,675,2,out _,out _,quest:probe with
                { Definition=probe.Definition! with { Dungeons=[new(82,parameter)] } }),
                "猎杀dungeon info只作目标信息，不作为普通建图门禁："+parameter);
        foreach (var invalid in new[] { probe with { Grade="[story]" }, probe with { SubType=1 }, probe with { IntData=[82,2,-6,1] } })
            That(!DungeonEntryPlan.TryCreate(catalog,pending,82,675,2,out _,out var reason,quest:invalid),
                $"普通借路仍核对受支持目标：{invalid.Grade}/{invalid.SubType}/{string.Join(',', invalid.IntData)}/{reason}");
        foreach (var compatible in new[] { probe with { MainQuest=100 }, probe with { Grade="[achievement]" },
            probe with { IntData=probe.IntData.Select((value, at) => at % 4 == 1 ? 3 : value).ToArray() },
            probe with { Definition=probe.Definition! with { Dungeons=[] } } })
            That(DungeonEntryPlan.TryCreate(catalog,pending,82,675,2,out _,out var reason,quest:compatible),
                "仅目标数据不匹配或缺省时不要求专用任务迷宫：" + reason);
        foreach (var sample in new[] { (Quest:649,Dungeon:163,Previous:674,Level:(byte)60), (Quest:650,Dungeon:82,Previous:675,Level:(byte)65) })
        {
            var material = basis with { Level=sample.Level,CompletedQuests=[sample.Previous],
                ActiveMainQuests=[new(5,checked((ushort)sample.Quest),1) { AcceptanceId=Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,material,checked((uint)sample.Dungeon),0,2,out var ordinary,out _,quests:quests) &&
                ordinary is { QuestId:0,IsOrdinary:true,IsOrdinaryHunt:false } &&
                DungeonEntryPlan.TryCreate(catalog,material,checked((uint)sample.Dungeon),checked((uint)sample.Quest),2,out var explicitEntry,out _,
                    quest:quests.Get(sample.Quest),quests:quests) && explicitEntry is { IsOrdinary: true, RouteQuestId: 0 },
                "材料步骤活动时普通0与携带任务号均可入场，不补造任务战斗路线");
        }
        var selectable = quests.Get(20752);
        That(selectable is { Condition: "[seeking]", SubType: 20, IntData: [2749638, 1, 2] } &&
            QuestLifecyclePlan.ValidSeeking(selectable) && QuestLifecyclePlan.MaterialLength(selectable) == 2,
            "20752保留特殊寻物的物品/数量/难度，不误读成普通物品数量对");
        var chooser = basis with { Level = 65, ActiveMainQuests = [new(5, 20752, 1) { AcceptanceId = Guid.NewGuid() }] };
        foreach (byte difficulty in new byte[] { 1, 2 })
            That(DungeonEntryPlan.TryCreate(catalog, chooser, 82, 0, difficulty, out var chosen, out _, quests: quests) &&
                chosen!.Difficulty == difficulty && chosen.QuestId == 0,
                "有难度选择权的普通入口保持玩家所选难度，不被活动任务尾部参数改写");
        var predecessor = QuestEligibilityTests.Quest(29989);
        var successor = probe with { Id = 29988, Grade = "[epic]", Prerequisites = [29989],
            Definition = probe.Definition! with { PrerequisiteGroups = [[29989]] } };
        var continuationQuests = new QuestCatalog(new[] { predecessor, successor }.ToDictionary(row => row.Id));
        var continuing = basis with { Level = 65, CompletedQuests = [29989],
            ActiveMainQuests = [new(5, 29988, QuestHuntProgress.Initial(successor)) { AcceptanceId = Guid.NewGuid() }] };
        var previous = new DungeonEntryPlan(basis.Id, basis.Revision, 82, 29989, 0, 2, null!, null!, null!)
            { RequestedDifficulty = 0 };
        var next = DungeonQuestContinuationRules.Resolve(continuing, previous, continuationQuests, catalog, [82], out var nextReason);
        That(next is { Difficulty: 2, RequestedDifficulty: 0, Quest.QuestId: 29988 },
            "后继任务保留自动提升前的原请求值，不用旧实际难度覆盖它：" + nextReason);
    }

    static void OrdinaryHuntRoutes(DungeonCatalog catalog, QuestCatalog quests, DungeonRoomCatalog rooms, Character basis)
    {
        int[] letters = [3921,2948,2967,3919,4894,4876,2958,2973,4915,3793,6114];
        foreach (int id in letters)
        {
            var quest = quests.Get(id);
            uint dungeonId = checked((uint)quest.Definition!.Dungeons.Single().DungeonId);
            var active = new ActiveMainQuest(5,checked((ushort)id),QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
            var character = basis with { Level = checked((byte)quest.MinimumLevel), ActiveMainQuests = [active] };
            var alternatives = new HashSet<int>();
            int choices = dungeonId == 40 ? 3 : dungeonId == 162 ? 2 : 1;
            for (int choice = 0; choice < choices; choice++)
            {
                That(DungeonEntryPlan.TryCreate(catalog,character,dungeonId,checked((uint)id),0,out var plan,out var reason,
                    quest:quest,draw:bound => choice % bound,quests:quests) &&
                    plan is { IsOrdinaryHunt:true,IsOrdinary:true,UsesFatigue:true,RouteQuestId:0 } && plan.QuestId == id &&
                    plan.ActiveQuestSlot == active.Slot && plan.QuestAcceptanceId == active.AcceptanceId &&
                    plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests),
                    "真实匿名信显式任务请求保留接取身份并沿普通疲劳路线："+id+"/"+reason);
                var entry = plan!;
                alternatives.Add(dungeonId == 40 ? entry.Maze.MapSpecification!.Single(map => map.Position == new DungeonPoint(1,2)).Id :
                    entry.Maze.SourceOrdinal);
                var monsters = entry.Maze.MapSpecification!.SelectMany(map => DungeonMonsterPlan.FromRoom(
                    rooms.Get(map,requirePassiveDefinitions:false),entry.CharacterId,1,entry.BasisLevel,playerTeam:entry.PlayerTeam).Monsters).ToArray();
                var target = monsters.First(monster => monster.MonsterId == quest.IntData[2] && monster.IsBlocking && monster.CanReportDeath);
                That(QuestHuntProgress.MonsterDeath([active],entry,quests,target).Single().ConditionState == active.ConditionState-1 &&
                    QuestHuntProgress.MonsterDeath([active with { AcceptanceId = Guid.NewGuid() }],entry,quests,target).Single().ConditionState == active.ConditionState,
                    "每个真实有限候选均可由原怪物死亡推进，放弃重接不消费旧实例："+id+"/"+choice);
            }
            That(alternatives.Count == choices,"普通随机保留全部有限迷宫或地图候选："+id);
            That(DungeonEntryPlan.TryCreate(catalog,character,dungeonId,0,0,out var ordinary,out _,draw:_ => 0,quests:quests) &&
                ordinary is { QuestId:0,RouteQuestId:0,IsOrdinaryHunt:false,IsOrdinary:true },"原匿名信活动快照的普通0入口保持原模式");
        }

        var letter = quests.Get(2948);
        var accepted = new ActiveMainQuest(5,2948,QuestHuntProgress.Initial(letter)) { AcceptanceId = Guid.NewGuid() };
        var pending = basis with { Level=45,ActiveMainQuests=[accepted] };
        var definition = catalog.Get(40);
        var normal = definition.Mazes.Single(maze => maze.QuestConnectionTokens.Count == 0);
        foreach (var sample in new[] { (Level:(byte)93,Count:10),(Level:(byte)95,Count:11) })
        {
            var active = letters.Take(sample.Count).Select((id,slot) => new ActiveMainQuest(checked((byte)slot),checked((ushort)id),
                QuestHuntProgress.Initial(quests.Get(id))) { AcceptanceId=Guid.NewGuid() }).ToArray();
            That(DungeonEntryPlan.TryCreate(catalog,basis with { Level=sample.Level,ActiveMainQuests=active },40,2948,0,out var multiple,out _,
                quest:letter,draw:_ => 0,quests:quests) && multiple is { IsOrdinaryHunt:true,RouteQuestId:0 } &&
                multiple.QuestAcceptances.SequenceEqual(active),"合法等级的10/11活动匿名信分别冻结全部原接取，不混称截图现场");
        }
        var twoMazes = quests.Get(4894);
        var twoActive = new ActiveMainQuest(5,4894,QuestHuntProgress.Initial(twoMazes)) { AcceptanceId=Guid.NewGuid() };
        foreach (int invalid in new[] {-1,2})
            Reject<InvalidDataException>(() => DungeonEntryPlan.TryCreate(catalog,basis with { Level=60,ActiveMainQuests=[twoActive] },
                162,4894,0,out _,out _,quest:twoMazes,draw:_ => invalid,quests:quests),"普通猎杀迷宫随机索引越界仍拒绝");
        foreach (var invalid in new[] {
            letter with { SubType=1 },
            letter with { IntData=[-1,-1,-6,2] }, letter with { IntData=[-1,-1,1014,0] } })
            That(!DungeonEntryPlan.TryCreate(definition,pending,40,2948,0,out _,out _,quest:invalid),
                "普通猎杀仍要求支持的任务分类、子类、目标选择符与正数量");
        foreach (var compatible in new[] {
            letter with { MainQuest=100 }, letter with { Grade="[daily]" }, letter with { IntData=[-1,-1,-5,2] },
            letter with { IntData=[41,-1,1014,2] }, letter with { IntData=[40,1,1014,2] },
            letter with { Definition=letter.Definition! with { Dungeons=[] } },
            letter with { Definition=letter.Definition! with { Dungeons=[new(41,-1)] } },
            letter with { Definition=letter.Definition! with { Dungeons=[new(40,-2)] } } })
            That(DungeonEntryPlan.TryCreate(definition,pending,40,2948,0,out _,out _,quest:compatible),
                "非结构性的目标图、难度与dungeon info不阻拦普通猎杀入场");
        foreach (var connection in new[] {
            new PvfToken[] {new(0,0),new(0,2948),new(0,1)},
            [new(0,1),new(0,2948),new(0,-1)], [new(0,2),new(0,100),new(0,-1)],
            [new(0,0),new(0,100)] })
            That(DungeonEntryPlan.TryCreate(definition with { Mazes=[normal,normal with { QuestConnectionTokens=connection }] },
                pending,40,2948,0,out _,out _,quest:letter) == (connection[1].Value != 2948),
                "本任务的特殊结构仍核对连接，其它连接不阻拦已有普通迷宫");
        foreach (var invalid in new[] { pending with { ActiveMainQuests=[] }, pending with { CompletedQuests=[2948] },
            pending with { ActiveMainQuests=[accepted with { AcceptanceId=Guid.Empty }] },
            pending with { ActiveMainQuests=[accepted,new(5,100,1) { AcceptanceId=Guid.NewGuid() }] } })
            That(!DungeonEntryPlan.TryCreate(definition,invalid,40,2948,0,out _,out _,quest:letter),"普通猎杀保留活动任务与唯一接取门禁");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes=definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count != 0).ToArray() },
            pending,40,2948,0,out _,out _,quest:letter),"缺少真实普通迷宫仍拒绝");
    }

    static void HarlemSeekingRoutes(DungeonCatalog catalog, QuestCatalog quests, Character basis)
    {
        // #256：黑市EXP 6053/6056/6059（seeking sub type 0）沿普通路线进入断头谷7322；6060同样入场，并带上精英出现率。
        foreach (int id in new[] { 6053, 6056, 6059 })
        {
            var quest = quests.Get(id);
            var accepted = basis with { Level = 93, CompletedQuests = [6050],
                ActiveMainQuests = [new(5,checked((ushort)id),1) { AcceptanceId = Guid.NewGuid() }] };
            That(DungeonEntryPlan.TryCreate(catalog,accepted,7322,checked((uint)id),0,out var plan,out var reason,quest: quest,quests: quests) &&
                plan is { IsOrdinaryHunt: true,RouteQuestId: 0 } && plan.QuestAcceptances.Single().QuestId == id,
                "黑市寻物任务沿普通路线入场断头谷：" + id + "/" + reason);
            That(DungeonEntryPlan.TryCreate(catalog,accepted,7322,checked((uint)id),0,out _,out _,
                quest: quest with { IntData = [quest.IntData[0],4] },quests: quests),"合法sub0寻物数量不再被三ID固定数量白名单误拒：" + id);
            That(!DungeonEntryPlan.TryCreate(catalog,accepted,7322,checked((uint)id),0,out _,out _,
                quest: quest with { IntData = [quest.IntData[0],0] },quests: quests),"非法零数量仍不能借用寻物路线：" + id);
            That(DungeonEntryPlan.TryCreate(catalog,accepted,3,checked((uint)id),0,out _,out _,quest: quest,quests: quests),
                "寻物任务可进入其它普通地下城，材料仍仅按原掉落声明授予：" + id);
        }
        var negative = basis with { Level = 93, CompletedQuests = [6050],
            ActiveMainQuests = [new(5,6060,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,negative,7322,6060,0,out var elitePlan,out var eliteReason,quest: quests.Get(6060),quests: quests) &&
            elitePlan is { IsOrdinaryHunt: true } && elitePlan.ChampionPercents == new DungeonChampionRates(25, 40, 60, 60),
            "6060走普通路线进断头谷，并带上精英出现率：" + eliteReason);

        // #256 系统排查：6051-6060 每个任务按其 PVF 声明的目标副本逐个走入场规划（已接取、等级达标）。
        // 6054 等 [hunt monster] 的 -3（普通 Boss）目标曾被入场规划误拒为 quest-maze-unavailable。
        var swept = new List<string>();
        foreach (int id in Enumerable.Range(6051,10))
        {
            var quest = quests.Get(id);
            foreach (var row in quest.Definition!.Dungeons)
            {
                var resource = catalog.Get(row.DungeonId);
                uint scalar = quest.Condition == "[hunt monster]" ? QuestHuntProgress.Initial(quest) : 1;
                var character = basis with { Level = checked((byte)Math.Max(quest.MinimumLevel,resource.MinimumLevel ?? 1)),
                    CompletedQuests = [6050], ActiveMainQuests = [new(5,checked((ushort)id),scalar) { AcceptanceId = Guid.NewGuid() }] };
                byte difficulty = checked((byte)Math.Max(row.Parameter,0));
                bool clearType = quest.Condition == "[condition under clear]"; // 通关类任务无任务号入场（普通入场，由通关进度计数）
                bool ok = DungeonEntryPlan.TryCreate(catalog,character,checked((uint)row.DungeonId),clearType ? 0u : checked((uint)id),difficulty,
                    out var plan,out var why,quest:clearType ? null : quest,quests:quests);
                swept.Add($"{id}|{quest.Condition}|dungeon {row.DungeonId}|{(ok ? "OK" : why)}");
                That(ok && (clearType ? plan is { IsOrdinary: true } : plan is { IsOrdinaryHunt: true }),"EXP 任务按声明副本可入场：" + id + "/" + row.DungeonId + "/" + why);
            }
        }
        Console.WriteLine("SWEEP-HARLEM-EXP " + string.Join(" ; ",swept));
    }
    static void AwakeningDungeonRoutes(DungeonCatalog catalog, QuestCatalog quests, DungeonRoomCatalog rooms, Character basis)
    {
        // #258：一次/二次觉醒的疑惑之村杀怪与收集阶段声明“dungeon info 53 2”，没有专用迷宫连接，只能沿普通迷宫进入。
        var resource = catalog.Get(53);
        var awakening = quests.All.Where(quest => quest.Grade == "[achievement]" && quest.Definition is { JobChange: 2 or 3 } declared &&
            declared.Dungeons.Any(row => row.DungeonId == 53 && row.Parameter == 2) &&
            quest.Condition is "[hunt monster]" or "[seeking]").OrderBy(quest => quest.Id).ToArray();
        That(awakening.Count(quest => quest.Condition == "[hunt monster]") >= 50 && awakening.Count(quest => quest.Condition == "[seeking]") >= 50,
            "真实疑惑之村觉醒杀怪与收集任务资源均已读取");
        foreach (var quest in awakening)
        {
            uint scalar = quest.Condition == "[hunt monster]" ? QuestHuntProgress.Initial(quest) : 1;
            var active = new ActiveMainQuest(3,checked((ushort)quest.Id),scalar) { AcceptanceId = Guid.NewGuid() };
            var character = basis with { Level = checked((byte)Math.Max(quest.MinimumLevel,resource.MinimumLevel ?? 1)), ActiveMainQuests = [active] };
            That(DungeonEntryPlan.TryCreate(catalog,character,53,checked((uint)quest.Id),2,out var plan,out var reason,quest:quest,draw:_ => 0,quests:quests) &&
                plan is { IsOrdinaryHunt:true,IsOrdinary:true,RouteQuestId:0,Difficulty:2 } && plan.QuestId == quest.Id &&
                plan.ActiveQuestSlot == active.Slot && plan.QuestAcceptanceId == active.AcceptanceId &&
                plan.QuestAcceptances.SequenceEqual(character.ActiveMainQuests),"觉醒任务请求在勇士难度进入普通迷宫并保留接取身份："+quest.Id+"/"+reason);
            foreach (byte low in new byte[] { 0,1 })
                That(DungeonEntryPlan.TryCreate(catalog,character,53,checked((uint)quest.Id),low,out _,out _,quest:quest,draw:_ => 0,quests:quests),
                    "觉醒收集或猎杀的目标难度不阻拦普通路线入场："+quest.Id+"/"+low);
            That(DungeonEntryPlan.TryCreate(catalog,character,53,0,2,out var ordinary,out _,draw:_ => 0,quests:quests) &&
                ordinary is { QuestId:0,IsOrdinaryHunt:false,IsOrdinary:true },"觉醒任务活动时普通0入口保持原模式");
        }
    }

    // 实验性（#259，依据来自 Go 项目反汇编，未经 V31 客户端验证）：比赛副本放行指定难度，起点/Boss 取地图反向声明的唯一比赛图。
    sealed class SequenceComparer : IEqualityComparer<int[]>
    {
        public bool Equals(int[]? x,int[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(int[] value) => value.Length;
    }

    static void TournamentEntry(DungeonCatalog catalog,Character character,DungeonRoomCatalog rooms)
    {
        foreach (var scenario in new[] { (Dungeon:123,Map:95265),(Dungeon:124,Map:95266) })
        {
            var resource = catalog.Get(scenario.Dungeon);
            var eligible = character with { Level = 75, ActiveMainQuests = [], CompletedQuests = [] };
            That(resource.IsTournament && resource.TryGetDesignatedDifficulty(out var designated) && designated == 2 &&
                resource.AllowsDifficulty(2,false) && !resource.AllowsDifficulty(0,false) && !resource.AllowsDifficulty(1,false),
                "比赛副本只放行指定难度：" + scenario.Dungeon);
            bool created = DungeonEntryPlan.TryCreate(catalog,eligible,checked((uint)scenario.Dungeon),0,2,out var plan,out var reason);
            That(created && plan is { Difficulty: 2 } && plan.StartMap.Id == scenario.Map && plan.BossMap.Id == scenario.Map,
                "比赛副本按反向声明确定唯一比赛图：" + scenario.Dungeon + "/" + reason);
            // 单图房间 + 四轮对手：对阵表/对手一致，能创建 Run，并能编出 NOTI386/387 包体。
            var declared = plan!;
            var room = rooms.Get(declared.StartMap,requirePassiveDefinitions: false,allowUnresolvedSpecialMetadata: false);
            var source = rooms.ReadTournamentSource(declared.StartMap);
            var roomPlan = DungeonTournamentPlan.CreateRoomPlan(rooms,room,source,12345,eligible.Id,100,declared.BasisLevel!.Value,declared.PlayerTeam);
            var route = new DungeonFixedRoute([roomPlan],[]);
            var tournament = roomPlan.Tournament!;
            That(source.PartyCount == 1 && source.Enemies.Count >= 15 && source.HasStartArea && tournament.Rounds.Count == 4 &&
                tournament.Rounds.Select(round => round.Count).SequenceEqual([16,8,4,2]) && tournament.Rounds.All(round => round.Count(team => team.Player) == 1) &&
                tournament.Opponents.Count == 4 && tournament.Opponents.Select(row => row.Round).SequenceEqual([1,2,3,4]) &&
                tournament.Opponents.Select(row => row.ObjectKey).Distinct().Count() == 4 &&
                tournament.Opponents.Take(3).All(row => row.Type == 0) && tournament.Opponents[3].Type == 3 &&
                tournament.Opponents.All(row => row.Level == declared.BasisLevel) &&
                roomPlan.Monsters.Count(monster => monster.IsBlocking) == 4 &&
                tournament.Rounds[0].Single(team => team.Player) is not null,
                "比赛对阵表与四轮对手：" + scenario.Dungeon);
            That(declared.Maze.StaticTopology.Supported && !declared.UsesFatigue && declared.IsTournament &&
                SinglePlayerDungeonRun.TryCreate(declared,1,100,route,new NativeDungeonCombatAuthority(),out _,out _),
                "比赛单图可装载房间并创建 Run：" + scenario.Dungeon);
            var info = DfoServer.Network.Builders.DungeonPackets.TournamentInfo(declared,tournament);
            // 头 6B + 四轮对阵（每轮 2B + 队数 * 7B，16+8+4+2 队，玩家队成员 code/战力为 0）+ 四轮对手（每轮 1B + 8B）
            That(info.Length == 6 + 4 * 2 + 30 * 7 + 4 * (1 + 8) &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(info) == (uint)scenario.Dungeon && info[4] == 2 && info[5] == 1 &&
                DfoServer.Network.Builders.DungeonPackets.TournamentMapInfo(declared,12345) is { Length: 15 } mapInfo &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mapInfo.AsSpan(8)) == (uint)scenario.Map && mapInfo[7] == 1,
                "NOTI386/387 包体长度与字段：" + scenario.Dungeon);
            // 同一 seed 对阵表可重现，不同 seed 通常不同。
            That(DungeonTournamentPlan.PlanBracket(source,7).Select(round => round.Select(team => team.Members.Sum(row => row.Code)).ToArray())
                .SequenceEqual(DungeonTournamentPlan.PlanBracket(source,7).Select(round => round.Select(team => team.Members.Sum(row => row.Code)).ToArray()),
                    new SequenceComparer()),"对阵表按 seed 可重现");            That(!DungeonEntryPlan.TryCreate(catalog,eligible,checked((uint)scenario.Dungeon),0,0,out _,out var rejected) && rejected == "difficulty-unsupported",
                "比赛副本非指定难度被拒绝：" + scenario.Dungeon);
        }
        That(!catalog.Get(3).IsTournament && !catalog.Get(3).AllowsDifficulty(2,false),"非比赛副本的难度规则不变");
    }

    // #438：根特南门(82)最低等级65，任务3524“夺取弹药”最低等级64；已接取该任务的64级角色应能进入，其余情形保持原拒绝。
    static void GentSouthGateQuestLevel(DungeonCatalog catalog,QuestCatalog quests,Character character)
    {
        var quest = quests.Get(3524);
        var accepted = character with { Level = 64,
            ActiveMainQuests = [new(0,3524,1) { AcceptanceId = Guid.NewGuid() }] };
        That(catalog.Get(82).MinimumLevel == 65 && quest.MinimumLevel == 64, "#438 前提：副本82最低65、任务3524最低64");
        That(DungeonEntryPlan.TryCreate(catalog,accepted,82,3524,0,out var direct,out var directReason,quest: quest,quests: quests) &&
            direct is { QuestId: 3524 }, "#438 64级已接取3524按任务进入南门放行：" + directReason);
        That(DungeonEntryPlan.TryCreate(catalog,accepted,82,0,0,out var selected,out var selectedReason,quests: quests) &&
            selected is { RouteQuestId: 3524 }, "#438 64级已接取3524按普通选图进入南门放行：" + selectedReason);
        var low = accepted with { Level = 63 };
        That(!DungeonEntryPlan.TryCreate(catalog,low,82,3524,0,out _,out var lowReason,quest: quest,quests: quests) &&
            lowReason == "dungeon-level-too-low", "#438 63级低于任务最低等级仍拒绝：" + lowReason);
        That(!DungeonEntryPlan.TryCreate(catalog,low,82,0,0,out _,out var lowSelectedReason,quests: quests) &&
            lowSelectedReason == "dungeon-level-too-low", "#438 63级普通选图仍拒绝：" + lowSelectedReason);
        var none = accepted with { ActiveMainQuests = [] };
        That(!DungeonEntryPlan.TryCreate(catalog,none,82,0,0,out _,out var noneReason,quests: quests) &&
            noneReason == "dungeon-level-too-low", "#438 无任务64级进南门仍拒绝：" + noneReason);
        var unrelated = accepted with { ActiveMainQuests = [new(0,3145,1) { AcceptanceId = Guid.NewGuid() }] };
        That(!DungeonEntryPlan.TryCreate(catalog,unrelated,82,0,0,out _,out var unrelatedReason,quests: quests) &&
            unrelatedReason == "dungeon-level-too-low", "#438 已接取但与南门无关的任务不放行：" + unrelatedReason);
        That(!DungeonEntryPlan.TryCreate(catalog,unrelated,82,3145,0,out _,out var unrelatedQuestReason,quest: quests.Get(3145),quests: quests) &&
            unrelatedQuestReason == "dungeon-level-too-low", "#438 请求无关任务进入南门不放行：" + unrelatedQuestReason);
    }

    // #447：绿都格罗兹尼「把治疗剂交给诺诺拉」。MAP 76331 声明 APC6001，匹配 AIC 目录没有该项；
    // 客户端缺定义不计创建成功，服务端不得因此整图拒绝入场。
    static void NonoraGroznyEntry(DungeonCatalog catalog,QuestCatalog quests,DungeonRoomCatalog rooms,Character character,PvfArchive pvf)
    {
        var parent = quests.Get(3347);
        var child = quests.Get(3425);
        That(parent is { DungeonIndex: 61, SubType: 2, Condition: "[quest clear]" } && parent.IntData.SequenceEqual([3425,3426]) &&
            QuestRelationProgress.SequentialParent(parent,quests) && child is { DungeonIndex: 61, MainQuest: 3347, Condition: "[hunt enemy]" } &&
            child.IntData.SequenceEqual([61,-1,109001851,3,1]),
            "#447 前提：3347 顺序 3425→3426 连接 D61，首子猎杀物件 109001851");
        var list = pvf.ReadTokens("aicharacter/aicharacter.lst");
        bool has6000 = false, has6001 = false;
        for (int at = 0; at + 1 < list.Length; at += 2)
            if (list[at].Type == 0)
            {
                if (list[at].Value == 6000) has6000 = true;
                else if (list[at].Value == 6001) has6001 = true;
            }
        That(has6000 && !has6001, "#447 匹配 AIC 目录有 6000 无 6001，不把缺项改写成相邻编号");
        uint remaining = QuestRelationProgress.Initial(parent,quests,new HashSet<int> { 3346 });
        var accepted = character with { Level = 48, CompletedQuests = [3346],
            ActiveMainQuests = [new(0,3347,remaining) { AcceptanceId = Guid.NewGuid() }] };
        foreach (uint request in new uint[] { 3347,0 })
        {
            That(DungeonEntryPlan.TryCreate(catalog,accepted,61,request,0,out var plan,out var reason,
                quest: request == 0 ? null : parent,quests: quests) &&
                plan is { StartMap.Id: 76330, BossMap.Id: 76331, Maze.ConnectionQuestId: 3425 },
                "#447 父/0 请求选择 3425 的 D61 路线 76330→76331：" + request + "/" + reason);
            var start = rooms.Get(plan!.StartMap,requirePassiveDefinitions: false);
            var boss = rooms.Get(plan.BossMap,requirePassiveDefinitions: false);
            That(start.Monsters.Count == 0 && start.AiCharacters.All(row => DungeonCompanion.IsCompanionRow(row.Id)),
                "#447 起点 76330 除GM伴侣行外无怪物也无 APC，不因缺演员拒绝");
            That(boss.Monsters.Count == 3 &&
                boss.Monsters[0] is { MonsterId: 75067, PlacementKind: "[fixed]", RankKind: "[normal]", Modifiers.Count: 0 } &&
                boss.Monsters[1] is { MonsterId: 75072, PlacementKind: "[fixed]", RankKind: "[normal]", Modifiers.Count: 0 } &&
                boss.Monsters[2] is { MonsterId: 75099, PlacementKind: "[fixed]", RankKind: "[boss]", Modifiers: ["[dummy]"] } &&
                boss.Monsters[2].RawValues.SequenceEqual([75099,1,0,-81,-239,0,1,1]) &&
                boss.AiCharacters.All(row => DungeonCompanion.IsCompanionRow(row.Id)) &&
                boss.PassiveObjects.Any(row => row.ObjectId == 109001851 && row.RawValues.Count == 4) &&
                boss.Npcs.Select(npc => npc.Id).SequenceEqual([1000]),
                "#447 76331 保留两只普通怪、dummy Boss 75099 与物件 109001851，缺目录 APC6001 不进入演员表");
            var failures = new List<string>();
            foreach (var map in plan.Maze.MapSpecification!.Concat(plan.Maze.LayeredMaps).Concat(plan.Maze.ConditionalMaps))
            {
                try
                {
                    _ = DungeonMonsterPlan.FromRoom(rooms.Get(map,requirePassiveDefinitions: false),1,100,plan.BasisLevel);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
                {
                    failures.Add($"{map.Id}: {error.Message}");
                }
            }
            That(failures.Count == 0, "#447 整条迷宫可以生成，不再因 APC6001 整图拒绝：" + string.Join(" | ", failures));
        }
    }

    static void RecoverAt5tWalkerEntry(DungeonCatalog catalog,QuestCatalog quests,DungeonRoomCatalog rooms,Character character)
    {
        var dungeon = catalog.Get(88);
        var found = quests.Get(3528);
        var recover = quests.Get(3529);
        var maze = dungeon.Mazes.Single(row => row.ConnectsQuest(3529));
        That(dungeon.Path.Equals("dungeon/act7/gentnorthgate_r.dgn", StringComparison.OrdinalIgnoreCase) &&
            dungeon.MinimumLevel == 66 && dungeon.BasisLevel == 69 &&
            found is { DungeonIndex: 88, Condition: "[clear map]" } && found.IntData.SequenceEqual([92270]) &&
            recover is { DungeonIndex: 88, Condition: "[clear map]" } && recover.IntData.SequenceEqual([92279]) &&
            maze.Starts.SequenceEqual([new DungeonPoint(0,0)]) && maze.Bosses.SequenceEqual([new DungeonPoint(8,0)]) &&
            maze.MapSpecification!.Select(map => map.Id).SequenceEqual(Enumerable.Range(92271, 9)) &&
            maze.RandomizedObjects.SequenceEqual([new DungeonRandomizedObject(new(0,0),61235,"neutral",615,244)]),
            "#444 D88 任务 3529 在起点格声明中立 AT-5T 61235，不把 92271 地图怪物表当成步行者来源");
        var foundMaze = dungeon.Mazes.Single(row => row.ConnectsQuest(3528));
        That(foundMaze.RandomizedObjects.Count == 0 && foundMaze.Bosses.SequenceEqual([new DungeonPoint(4,0)]) &&
            foundMaze.MapSpecification!.Single(map => map.Position == new DungeonPoint(4,0)).Id == 92270,
            "#444 3528 迷宫没有随机骑乘块，步行者只在 92270 的 monster 行");
        var accepted = character with { Level = 66, CompletedQuests = [3528],
            ActiveMainQuests = [new(0,3529,1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(catalog,accepted,88,3529,0,out var plan,out var reason,
            quest: recover,quests: quests) &&
            plan is { StartMap.Id: 92271, BossMap.Id: 92279, Maze.ConnectionQuestId: 3529, PlayerTeam: 0 },
            "#444 3529 入场选择 92271→92279：" + reason);
        var start = rooms.Get(plan!.StartMap, requirePassiveDefinitions: false);
        var foundBoss = rooms.Get(foundMaze.MapSpecification!.Single(map => map.Id == 92270), requirePassiveDefinitions: false);
        That(!start.Monsters.Any(row => row.MonsterId == 61235) &&
            foundBoss.Monsters.Any(row => row.MonsterId == 61235 && row.PlacementKind == "[fixed]" &&
                row.RankKind == "[normal]" && row.Modifiers.Count == 0 && row.Team == 0),
            "#444 61235 在 3528 末图是 team0 固定怪，3529 起点地图怪物表没有该行");
        var monsters = DungeonMonsterPlan.FromRoom(start, accepted.Id, 100, plan.BasisLevel, playerTeam: plan.PlayerTeam);
        byte[] body = DfoServer.Network.Builders.DungeonPackets.StartMap(plan, monsters, 1);
        int actors = monsters.Monsters.Count(monster => monster.HellWave is null) + monsters.SpecialObjects.Count + monsters.HellWaves.Count;
        int tail = 20 + actors * 22;
        That(body[18] == actors && body[tail] == 0 && body[tail + 1] == 1 && body[tail + 2] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tail + 3)) == 615 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tail + 7)) == 244 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tail + 11)) == 61235 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tail + 15)) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(tail + 19)) == 0 &&
            body[tail + 23] == 255,
            "#444 3529 起点 NOTI29 发出 61235 骑乘组，不再写空骑乘计数");
        var next = plan with { StartMap = maze.MapSpecification!.Single(map => map.Position == new DungeonPoint(1,0)) };
        byte[] nextBody = DfoServer.Network.Builders.DungeonPackets.StartMap(next, 1);
        That(nextBody.Length == 23 && nextBody[21] == 0 && nextBody[22] == 255,
            "#444 未声明骑乘的后续格仍保持空骑乘组，不把起点对象复制进其它房间");
    }
}
