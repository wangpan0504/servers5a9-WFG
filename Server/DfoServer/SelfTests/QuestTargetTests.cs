using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestTargetTests
{
    public static void RunCore()
    {
        QuestModuleSchemaTests.Run();
        int before = Count;
        var basis = QuestEligibilityTests.Quest(100);
        var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [100] = basis });
        That(QuestTargetTypes.All.Select(row => row.Tag).Distinct().Count() == QuestTargetTypes.All.Length &&
            QuestTargetTypes.All.Select(row => row.Kind).Distinct().Count() == QuestTargetTypes.All.Length,
            "类型注册的资源名与领域类型各自唯一");
        That(QuestTargetRules.Inspect(basis, catalog) == QuestTargetSupport.Implemented && QuestTargetRules.CompletionType(basis, catalog) == 4,
            "普通会面沿统一类型分发并保留本版交付编号");
        foreach (string condition in new[] { "[raid phase clear]" })
        {
            var unsupported = basis with { Condition = condition, IntData = condition switch
                { "[use skill]" => [1, 10], "[raid phase clear]" => [0, 2, 5, 12], _ => [2] } };
            That(unsupported.TargetKind != QuestTargetKind.Unknown && QuestTargetRules.Inspect(unsupported, catalog) == QuestTargetSupport.Unsupported,
                "已注册但未实现的普通目标保留明确状态：" + condition);
            Reject<NotSupportedException>(() => QuestTargetRules.CompletionType(unsupported, catalog), "不以通用会面交付替代未实现目标");
        }
        That(QuestTargetRules.Inspect(basis with { Condition = "[future target]" }, catalog) == QuestTargetSupport.Unregistered,
            "未知目标保留原标签，不映射为已支持目标");
        foreach (int difficulty in new[] { -1, 0, 2, 4 })
        {
            var seeking = basis with { Condition = "[seeking]", SubType = 20, IntData = [9002, 3, difficulty] };
            That(QuestSeekingObjective.TryRead(seeking, out var target) && target == new QuestSeekingObjective(2, difficulty, null) &&
                QuestLifecyclePlan.SeekingSubtype(seeking) && QuestTargetRules.Supports(seeking, catalog) &&
                QuestTargetRules.CompletionType(seeking, catalog) == 0, "sub20保留材料与最低难度的独立语义");
        }
        foreach (int[] data in new int[][] { [], [9002, 1], [9002, 1, -2], [9002, 1, 5], [9002, 0, 2], [0, 1, 2] })
            That(!QuestSeekingObjective.TryRead(basis with { Condition = "[seeking]", SubType = 20, IntData = data }, out _),
                "sub20残缺及越界声明不补默认值");
        That(QuestSeekingObjective.TryRead(basis with { Condition = "[seek n meet npc]", IntData = [9002, 3, 45] }, out var meet) &&
            meet == new QuestSeekingObjective(2, null, 45), "寻物会面尾部NPC不被sub20的难度规则改变");
        ClearItems(basis);
        EpicDifficulty(basis);
        DifficultyModes(basis);
        HellTerrain(basis);
        Console.WriteLine($"PASS {Count - before} 任务类型分发、sub20及主线入场难度断言");
    }

    static void ClearItems(StoryQuest basis)
    {
        var quest = basis with { Condition = "[seeking]", SubType = 20, IntData = [9002, 2, 2] };
        var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [quest.Id] = quest });
        var active = new ActiveMainQuest(0, 100, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Target", 1, new(38, 2, 450, 240), null) { ActiveMainQuests = [active] };
        var items = new ItemCatalog(null!, null!, null!, 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [9002] = new(9002, ItemKind.Quest, 0, 1, 0, 99, 0, 0, 0, "", 0, 0, "quest") });
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var entry = new DungeonEntryPlan(1, 1, 3, 0, byte.MaxValue, 2, null!, null!, null!)
            { QuestAcceptances = [active], RecommendedQuestItems = true };
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var now = DateTimeOffset.UtcNow;
        QuestDropPlan Roll(Character actor, DungeonEntryPlan route, bool clear) => QuestDropPlan.Create(actor, route,
            QuestDropResources.Create(actor, route, catalog, items, pieces), Guid.NewGuid(), identity, 1, 1, 1, clear,
            _ => throw new InvalidOperationException("确定性通关取物不抽随机数"), now);
        That(!Roll(character, entry, false).Inventory.Changed && Roll(character, entry, true).Outcomes is [{ Granted: 1 }],
            "sub20不从普通击杀伪造通关，只在真实清图增加一件任务物品");
        foreach (var other in new[] { entry with { Difficulty = 1 }, entry with { RecommendedQuestItems = false } })
            That(!Roll(character, other, true).Inventory.Changed, "低于最低难度或不符合推荐等级不赠证明");
        foreach (byte higher in new byte[] { 3, 4 })
            That(Roll(character, entry with { Difficulty = higher }, true).Outcomes is [{ Granted: 1 }],
                "sub20在高于要求的难度通关同样获得任务物品");
        var first = Roll(character, entry, true);
        var updated = character with { Inventory = first.Inventory.After };
        That(QuestInventoryProgress.Refresh([active], updated.Inventory, catalog, now.ToUnixTimeSeconds()).Single().ConditionState == 1,
            "未足额材料保持未完成，不以一次通关直接清任务");
        var second = Roll(updated, entry, true);
        var complete = character with { Inventory = second.Inventory.After };
        That(complete.Inventory.Items.Single().Core.Count == 2 &&
            QuestInventoryProgress.Refresh([active], complete.Inventory, catalog, now.ToUnixTimeSeconds()).Single().ConditionState == 0 &&
            !Roll(complete, entry, true).Inventory.Changed, "材料满足后归零，通关不能继续超量发放");
        var rebound = character with { ActiveMainQuests = [active with { AcceptanceId = Guid.NewGuid() }] };
        That(!Roll(rebound, entry, true).Inventory.Changed, "旧Run不能向放弃重接的新接取身份赠物");
        var any = quest with { IntData = [9002, 1, -1] };
        That(QuestTargetRules.Drops(any, entry with { Difficulty = 4 }).Single() is { Difficulty: -1, EnemyType: 0 },
            "sub20的-1不限难度但仍是通关事件");
    }

    static void EpicDifficulty(StoryQuest basis)
    {
        var start = new DungeonMap("map", new(0, 0), 1001, "map/start.map");
        var boss = new DungeonMap("boss", new(1, 0), 1002, "map/boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB EE", [start.Position], [boss.Position], [start, boss], []);
        DungeonRawSection[] sections = [new("difficulty level", Enumerable.Repeat(new PvfToken(0, 1), 5).ToArray())];
        var dungeon = new DungeonDefinition(17, "dungeon/epic.dgn", 60, 65, [maze], sections);
        var hunt = basis with { Grade = "[epic]", Condition = "[hunt monster]", IntData = [17, 2, 1, 1] };
        var active = new ActiveMainQuest(0, 100, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Epic", 1, new(38, 2, 450, 240), null) { Level = 65, ActiveMainQuests = [active] };
        byte Resolve(StoryQuest quest, byte request = 0, DungeonDefinition? definition = null) =>
            QuestDungeonDifficulty.Resolve(character, definition ?? dungeon, 100, quest, null, request);
        That(Resolve(hunt) == 2 && Resolve(hunt, 3) == 3 && Resolve(hunt with { Grade = "[side]" }) == 0,
            "主线猎杀自动升最低难度，不降低原选择且不改支线");
        That(QuestDungeonDifficulty.Resolve(character with { ActiveMainQuests = [active with { ConditionState = 0 }] },
            dungeon, 100, hunt, null, 0) == 0, "已达成主线目标不再强制提升重新入场难度");
        That(Resolve(basis with { Grade = "[epic]", Condition = "[condition under clear]", SubType = 6, IntData = [17, 2, 1] }) == 2,
            "主线通关目标复用类型解析的最低难度");
        That(Resolve(basis with { Grade = "[epic]", Condition = "[condition under clear]", SubType = 0, IntData = [17, 2, 120] }) == 2,
            "限时等条件通关同样提取最低难度，不局限sub6或sub11");
        var region = basis with { Grade = "[epic]", Condition = "[condition under clear]", SubType = 0, IntData = [-4, 49, 2, 120],
            Definition = basis.Definition! with { Dungeons = [new(17, 0)] } };
        That(!QuestDungeonScope.CanEvaluate(new(QuestDungeonSelectorKind.Area, 49, 2), region) && Resolve(region) == 0,
            "dungeon info不能冒充区域归属，未闭合的区域过滤不改变实际难度或授予进度");
        var seeking = basis with { Grade = "[epic]", Condition = "[seeking]", IntData = [9002, 1],
            Definition = basis.Definition! with { Drops = [new(1, 1, 17, 2, 9002, 1, 100, 1)] } };
        That(Resolve(seeking) == 2 && Resolve(seeking, 3) == 3 && Resolve(seeking with { IntData = [9002, 1, 2], SubType = 20 }) == 2,
            "主线取物统一按最低难度规划，不强行降低玩家的更高选择");
        That(Resolve(basis with { Grade = "[epic]", DungeonIndex = 17, DungeonParameter = 3,
            Definition = basis.Definition! with { Dungeons = [new(17, 3)] } }) == 0,
            "界面dungeon info不签发自动升难度要求");
        That(Resolve(hunt, definition: dungeon with { RawSections = [.. sections, new("designate dungeon difficulty", [new(0, 0)])] }) == 0,
            "固定难度资源不被任务自动升难度覆盖");
        That(Resolve(hunt, definition: dungeon with { RawSections = [.. sections, new("designate dungeon difficulty", [new(0, -1)])] }) == 2,
            "指定难度-1是未指定，不误认成固定模式");
        var connected = dungeon with { Mazes = [maze with { QuestConnectionTokens = [new(0, 0), new(0, 100), new(0, 2)] }] };
        That(Resolve(basis with { Grade = "[epic]" }, definition: connected) == 2,
            "主线专用任务连接的精确难度也是可实现路线的约束");
        var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [100] = hunt });
        That(DungeonEntryPlan.TryCreate(dungeon, character, 17, 100, 0, out var plan, out var reason, quest: hunt, quests: catalog) &&
            plan is { Difficulty: 2, RequestedDifficulty: 0, IsOrdinaryHunt: true } && plan.QuestAcceptanceId == active.AcceptanceId,
            "真实路线规划在冻结之前采用任务难度并保留请求与接取身份：" + reason);
        var timed = hunt with { Condition = "[condition under clear]", SubType = 0, IntData = [17, 2, 120] };
        var timedCatalog = new QuestCatalog(new() { [100] = timed });
        That(DungeonEntryPlan.TryCreate(dungeon, character, 17, 100, 0, out var timedEntry, out var timedReason,
            quest: timed, quests: timedCatalog) && timedEntry is { IsOrdinaryHunt: true, Difficulty: 2, RouteQuestId: 0 },
            "条件通关无专用结构时可以借普通迷宫并提升实际最低难度：" + timedReason);
        That(plan!.AcceptsDifficultyConfirmation(0) && plan.AcceptsDifficultyConfirmation(1) && plan.AcceptsDifficultyConfirmation(2) &&
            !plan.AcceptsDifficultyConfirmation(3) && !(plan with { RequestedDifficulty = null }).AcceptsDifficultyConfirmation(0),
            "自动提升只确认归到同一冻结档的请求，固定及非自动模式不放宽确认");
        That(DungeonEntryPlan.TryCreate(dungeon, character, 17, 0, 0, out var practice, out _, quests: catalog, practice: true) &&
            practice is { IsPractice: true, Difficulty: 0, RequestedDifficulty: null }, "无奖励练习不套用主线自动难度");
        That(DungeonEntryPlan.TryCreate(dungeon, character, 17, 0, 0, out var automatic, out _, quests: catalog) &&
            automatic is { Difficulty: 2 }, "普通0请求的活动主线目标仍能决定实际所需难度");
        That(DungeonEntryPlan.TryCreate(dungeon, character with { ActiveMainQuests = [] }, 17, 0, 2, out var follower, out _,
            partyRoute: plan! with { RecommendedQuestItems = false }) && follower is { Difficulty: 2, RecommendedQuestItems: true },
            "成员沿批准难度而非本人任务另开难度，推荐物品资格按本人重算");

        var second = seeking with { Id = 101, SubType = 20, IntData = [9002, 1, 3], Definition = basis.Definition };
        var elsewhere = hunt with { Id = 102, IntData = [18, 4, 1, 1] };
        var side = hunt with { Id = 103, Grade = "[side]", IntData = [17, 4, 1, 1] };
        var completed = hunt with { Id = 104, IntData = [17, 4, 1, 1] };
        var many = new QuestCatalog(new[] { seeking, second, elsewhere, side, completed }.ToDictionary(row => row.Id));
        var actor = character with { ActiveMainQuests = many.All.Select((row, at) =>
            new ActiveMainQuest((byte)at, (ushort)row.Id, row.Id == completed.Id ? 0u : 1u) { AcceptanceId = Guid.NewGuid() }).ToArray() };
        foreach (byte request in new byte[] { 0, 1, 2, 3, 4 })
            That(QuestDungeonDifficulty.Resolve(actor, dungeon, 100, seeking, many, request) == Math.Max(request, (byte)3) &&
                QuestDungeonDifficulty.Resolve(actor, dungeon, 0, null, many, request) == Math.Max(request, (byte)3),
                "关联主线取最高最低要求，显式/普通入口一致，不采纳异图、支线及已达成目标");
        var alternatives = seeking with { Definition = seeking.Definition! with { Drops =
            [new(1,1,17,1,9002,1,100,1),new(2,1,17,3,9002,1,100,1)] } };
        That(Resolve(alternatives) == 1 && Resolve(alternatives, 2) == 2,
            "同物品不同掉落来源保留OR门槛，更高玩家选择无需跳到另一个精确档");
        That(QuestDungeonDifficulty.Resolve(actor, dungeon with { RawSections = [new("difficulty level",
            [new(0,1),new(0,1),new(0,1),new(0,0),new(0,1)])] }, 0, null, many, 0) == 4,
            "最高要求档未开放时选更高合法档，不回到不足难度");
    }

    static void HellTerrain(StoryQuest basis)
    {
        var start = new DungeonMap("map",new(0,0),1001,"map/start.map");
        var middle = new DungeonMap("map",new(1,0),1003,"map/middle.map");
        var boss = new DungeonMap("boss",new(2,0),1002,"map/boss.map");
        var ordinary = new DungeonMaze(0,3,1,"BB BB EE",[start.Position],[boss.Position],[start,middle,boss],[])
            { HellParty=new(1004,middle.Position),HellPartyPath="map/hell.map" };
        var special = ordinary with { SourceOrdinal=1,QuestConnectionTokens=[new(0,0),new(0,100),new(0,0)],HellParty=null,HellPartyPath=null };
        var dungeon = new DungeonDefinition(17,"dungeon/hell-priority.dgn",1,65,[ordinary,special],
            [new("difficulty level",Enumerable.Repeat(new PvfToken(0,1),5).ToArray())]);
        var quest = basis with { Condition="[hunt monster]",IntData=[17,1,1,1],
            Definition=basis.Definition! with { Drops=[new(1,1,17,1,9002,1,100,1)] } };
        var other = quest with { Id=101 };
        var catalog = new QuestCatalog(new[] {quest,other}.ToDictionary(row=>row.Id));
        var active = new ActiveMainQuest(0,100,1) { AcceptanceId=Guid.NewGuid() };
        var next = new ActiveMainQuest(1,101,1) { AcceptanceId=Guid.NewGuid() };
        var actor = new Character(1,0,"HellPriority",1,new(38,2,450,240),null)
            {Level=65,ActiveMainQuests=[active,next]};
        That(DungeonEntryPlan.TryCreate(dungeon,actor,17,100,3,out var entry,out var reason,quest:quest,quests:catalog,hellParty:true) &&
            entry is { QuestId:0,RouteQuestId:0,Difficulty:3,Maze.SourceOrdinal:0,HellParty:not null } &&
            entry.BypassedQuestTerrain.SequenceEqual([100]),
            "深渊优先普通深渊迷宫，不被特殊任务连接或任务号阻拦，也不降低客户端难度："+reason);
        That(entry!.QuestAcceptances.SequenceEqual(actor.ActiveMainQuests) && QuestHuntProgress.Bound(active,entry),
            "暂停地形不清除任务绑定、接取或条件状态");
        var monster = new DungeonPlannedMonster(0,10,1,1,new(1,[1,0,1,0,0,0,1,1],"[fixed]","[normal]","monster/fixture.mob"));
        That(QuestHuntProgress.MonsterDeath(actor.ActiveMainQuests,entry,catalog,monster).All(row=>row.ConditionState==0) &&
            actor.ActiveMainQuests.All(row=>row.ConditionState==1),
            "需要特殊地形的击杀任务仍按深渊里的真实匹配怪物推进，不原地改写旧快照");
        var items = new ItemCatalog(null!,null!,null!,0);
        items.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9002]=new(9002,ItemKind.Quest,0,1,0,99,0,0,0,"",0,0,"quest") });
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var resources = QuestDropResources.Create(actor,entry,catalog,items,pieces);
        var drops = QuestDropPlan.Create(actor,entry,resources,Guid.NewGuid(),new(1,0,1,Guid.NewGuid(),1),1003,1,1,false,_=>0,DateTimeOffset.UtcNow);
        That(drops.Outcomes.Any(row=>row.QuestId==100 && row.Granted==1),
            "地形提示不拦截原本符合条件的任务物品掉落");
        That(DungeonEntryPlan.TryCreate(dungeon,actor,17,100,0,out var restored,out _,quest:quest,quests:catalog) &&
            restored is { RouteQuestId:100,Maze.SourceOrdinal:1 } && restored.BypassedQuestTerrain.IsEmpty,
            "再次普通入场正常使用任务地形，不把本次深渊选择永久写入任务状态");
        var parent = basis with { Id=102,Condition="[quest clear]",SubType=2,IntData=[103,104] };
        var first = quest with { Id=103,MainQuest=102 };
        var last = quest with { Id=104,MainQuest=102 };
        var related = new QuestCatalog(new[] {parent,first,last}.ToDictionary(row=>row.Id));
        var relationMap = dungeon with { Mazes=[ordinary,special with { QuestConnectionTokens=[new(0,0),new(0,103),new(0,0)] }] };
        var parentActor = actor with { ActiveMainQuests=[new(0,102,2) {AcceptanceId=Guid.NewGuid()}] };
        That(QuestTerrainBypass.ForHell(parentActor,relationMap,related).SequenceEqual([102]) &&
            QuestTerrainBypass.ForHell(parentActor with {CompletedQuests=[103]},relationMap,related).IsEmpty,
            "顺序父任务只提示当前子任务的地形，不把未来/已完成子路线误当当前要求");
    }

    static void DifficultyModes(StoryQuest basis)
    {
        var start = new DungeonMap("map", new(0,0), 1001, "map/start.map");
        var boss = new DungeonMap("boss", new(1,0), 1002, "map/boss.map");
        var normal = new DungeonMaze(0,2,1,"BB EE",[start.Position],[boss.Position],[start,boss],[]);
        var story = normal with { SourceOrdinal = 1, QuestConnectionTokens = [new(0,0),new(0,100),new(0,-1)] };
        var levels = new DungeonRawSection("difficulty level", Enumerable.Repeat(new PvfToken(0,1),5).ToArray());
        DungeonRawSection[] owned = [new("story mode",[]),new("difficulty size",[new(0,2)]),
            new("quest list",[new(0,100)]),new("/quest list",[]),new("/story mode",[]),levels];
        var dungeon = new DungeonDefinition(17,"dungeon/modes.dgn",1,65,[normal,story],owned);
        var quest = basis with { Condition = "[hunt monster]", IntData = [17,1,1,1] };
        var catalog = new QuestCatalog(new() { [100] = quest });
        var actor = new Character(1,0,"Modes",1,new(38,2,450,240),null) { Level = 65,
            ActiveMainQuests = [new(0,100,1) { AcceptanceId = Guid.NewGuid() }] };
        foreach (byte value in new byte[] { 0,1,2,3,4 })
        {
            That(DungeonEntryPlan.TryCreate(dungeon, actor with { ActiveMainQuests=[] }, 17, 0, value, out var ordinary, out _, quests:catalog) &&
                ordinary is { RouteQuestId:0, IsOrdinary:true } && ordinary.Difficulty == value && ordinary.Maze == normal,
                "剧情块不移除普通副本开放的五档选择");
            That(DungeonEntryPlan.TryCreate(dungeon, actor, 17, 100, value, out var entry, out _, quest:quest, quests:catalog) &&
                entry is { RouteQuestId:100, Maze.SourceOrdinal:1 } && entry.Difficulty == Math.Max(value,(byte)1),
                "普通任务即使列在故事名册中也保留开放档，自动提升不降低玩家选择");
            That(QuestDungeonDifficulty.Resolve(actor,dungeon,100,quest,catalog,value) >= value,
                "普通及剧情自动规划均不得低于本次客户端请求难度");
            var early = dungeon with { Id=3, Mazes=[normal] };
            That(DungeonEntryPlan.TryCreate(early, actor with { ActiveMainQuests=[] }, 3, 0, value, out var earlyEntry, out _) &&
                earlyEntry!.Difficulty == value, "普通选择依据资源开放表，不按早期副本ID写死零难度");
        }
        var dedicated = dungeon with { RawSections = [.. owned.Select(row => row.Tag == "difficulty size" ?
            row with { Tokens=[new(0,3)] } : row), new("designate dungeon difficulty",[new(0,4)])] };
        var harder = quest with { IntData=[17,2,1,1] };
        var harderCatalog = new QuestCatalog(new() { [100] = harder });
        That(DungeonEntryPlan.TryCreate(dedicated,actor,17,100,0,out var raised,out _,quest:harder,quests:harderCatalog) &&
            raised is { Difficulty:2, RequestedDifficulty:0, RouteQuestId:100 } &&
            dedicated.AllowsDifficulty(4,false) && !dedicated.AllowsDifficulty(2,false),
            "剧情按自己的范围升难度，不受块外普通固定四档覆盖");
        foreach (byte value in new byte[] { 0,1,2,3,4 })
        {
            That(DungeonEntryPlan.TryCreate(dedicated,actor,17,100,value,out var entry,out _,quest:harder,quests:harderCatalog) == (value < 3) &&
                (value >= 3 || entry is { RouteQuestId:100, Difficulty:2 }),
                "剧情专用范围独立核验，越界请求不靠降低或借普通指定档放行");
            That(DungeonEntryPlan.TryCreate(dedicated,actor with { ActiveMainQuests=[] },17,0,value,out _,out _) == (value == 4),
                "同一DGN的普通指定四档仍可进入，不被剧情范围替换");
        }
        var legacy = dungeon with { RawSections=[new("story mode",[]),new("difficulty size",[new(0,1)]),new("/story mode",[]),levels] };
        That(DungeonEntryPlan.TryCreate(legacy,actor,17,100,3,out var choice,out _,quest:quest,quests:catalog) && choice is { Difficulty:3 },
            "未显式归属剧情块的普通任务连接继续保留更高难度选择");
        var fixedNormal = dungeon with { Mazes=[normal], RawSections=[levels,new("designate dungeon difficulty",[new(0,2)])] };
        foreach (byte value in new byte[] { 0,1,2,3,4 })
            That(DungeonEntryPlan.TryCreate(fixedNormal,actor with { ActiveMainQuests=[] },17,0,value,out _,out _) == (value == 2),
                "普通固定档仍只允许声明值，不按任务最低门槛放宽固定模式");
    }
}
