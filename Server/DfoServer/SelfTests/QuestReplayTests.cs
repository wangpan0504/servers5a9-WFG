using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System.Buffers.Binary;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestReplayTests
{
    public static void RunResourcePreparation(PvfArchive pvf)
    {
        int checks = Count;
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var connections = global::DfoServer.Testing.SelfTestCatalogs.QuestConnections(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var results = new List<(uint QuestId,uint DungeonId,QuestReplayPlan? Replay,bool Accepted,DungeonEntryPlan? Entry,string Reason)>();
        // 两条真实路线均先采结果，避免城镇重播失败时丢失普通路线的对照证据。
        foreach (var (questId,dungeonId) in new[] { (3145u,3u),(3158u,7159u) })
        {
            var character = new Character(1,0,"ReplayResource",12,new Location(38,2,450,240),null)
            { Level = 95,CompletedQuests = [checked((int)questId)] };
            var replay = QuestReplayPlan.Prepare(character,questId,quests,connections);
            DungeonEntryPlan? entry = null;
            string reason = "quest-replay-prepare-unavailable";
            bool accepted = replay is not null && DungeonEntryPlan.TryCreateReplay(dungeons,replay,out entry,out reason);
            Console.WriteLine($"quest_replay_resource QuestId={questId} ExpectedDungeonId={dungeonId} " +
                $"Prepared={replay is not null} DungeonId={replay?.DungeonId} Accepted={accepted} Reason={reason}");
            results.Add((questId,dungeonId,replay,accepted,entry,reason));
        }
        foreach (var result in results)
        {
            That(result.Replay is not null && result.Replay.DungeonId == result.DungeonId,
                $"真实已完成任务{result.QuestId}应取得显式回放路线{result.DungeonId}，实际原因={result.Reason}");
            That(result.Accepted && result.Entry is { IsQuestReplay: true,QuestId: 0 } &&
                result.Entry.RouteQuestId == result.QuestId && result.Entry.DungeonId == result.DungeonId,
                $"真实显式回放路线{result.QuestId}/{result.DungeonId}应可准备，实际原因={result.Reason}");
        }
        var town = results.Single(result => result.QuestId == 3158).Entry!;
        var topology = town.Maze.StaticTopology;
        That(topology.Supported && topology.Rooms is [{ ExitMask:0 } actualRoom] && actualRoom.Map == town.StartMap &&
            topology.Links is { Count:0 } && town.Maze.GreedGrid.Cells is [{ Index:0 }],
            "真实城镇回放使用原AA及显式唯一房间生成零出口拓扑，不手工绕过资源门槛");
        var room = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf).Get(town.StartMap,requirePassiveDefinitions:false);
        var monsters = DungeonMonsterPlan.FromRoom(room,town.CharacterId,10,town.MonsterBasisLevel,playerTeam:town.PlayerTeam);
        var target = monsters.Monsters.Single(monster => monster.MonsterId == 62245);
        That(town.StartMap == town.BaseBossMap && town.BaseBossMap == town.BossMap && town.StartMap.Kind == "boss" &&
            town.StartMap.Id == 311189 && room.DeclaredType == "[normal]" &&
            target is { SpawnType:3,IsBoss:true,CanReportDeath:true,IsFriendly:false,IsDummyMapBoss:false } && target.IsScriptedBoss &&
            target.Source is { PlacementKind:"[fixed]",RankKind:"[boss]",Modifiers:["[dummy]"] },
            "真实城镇回放仅投影终点，保留normal地图及62245脚本Boss的生成、死亡和阵营身份");
        That(town.QuestAcceptances.IsEmpty,
            "真实回放Entry不冻结活动任务资格，资源准备及死亡提交不生成任务掉落");
        var authority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(town,1,1,new DungeonFixedRoute([monsters],
            topology.Links!.Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray()),authority,out var run,out var runReason) &&
            authority.BindTo(run!),"真实城镇回放批准路线通过原Run地图及演员复验，原因="+runReason);
        var activeRun = run!;
        var loading = activeRun.Snapshot.Identity;
        var death = new NativeDungeonScriptedDeathObservation(target.ObjectKey,0,true,new string('A',64));
        That(authority.ValidateAndIssueScripted(activeRun,loading,death).Outcome == DungeonRunOutcome.Rejected &&
            activeRun.CompleteLoading(loading,Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted && activeRun.Snapshot.ConsumedFatigue == 0,
            "真实回放加载前不能报告死亡，首次加载保留回放零疲劳语义");
        var loaded = activeRun.Snapshot.Identity;
        That(authority.ValidateAndIssueScripted(activeRun,loading,death).Outcome == DungeonRunOutcome.Rejected &&
            authority.ValidateAndIssueScripted(activeRun,loaded,death with { ChecksumMatches=false }).Outcome == DungeonRunOutcome.Rejected,
            "回放投影不放宽旧epoch或错误校验的脚本死亡");
        var issued = authority.ValidateAndIssueScripted(activeRun,loaded,death);
        That(issued.Death is not null,"真实回放62245沿原脚本死亡签发，不伪造演出通关");
        var cleared = activeRun.AcceptConfirmedDeath(issued.Death!);
        That(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.ClearFact is not null &&
            activeRun.Phase == SinglePlayerDungeonPhase.Settlement &&
            activeRun.AcceptConfirmedDeath(issued.Death!).Outcome == DungeonRunOutcome.Duplicate,
            "真实huntboss62245终点清场只提交一次，重复死亡不生成第二份通关事实");
        Console.WriteLine($"PASS {Count-checks} 真实任务回放普通路线与城镇路线准备断言");
    }

    public static void RunCore()
    {
        int checks = Count;
        byte[] request = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(request,400);
        That(QuestReplayRequests.TryParse(request,out uint requested) && requested == 400 &&
            !QuestReplayRequests.TryParse(request.AsSpan(0,3),out _) && !QuestReplayRequests.TryParse([.. request,0],out _),
            "1459按完整单u32生产者读取，不猜路线或接受未知尾部");
        That(QuestReplayPackets.Accepted() is [1,0,0,0,0] && QuestReplayPackets.Rejected() is [0,19,0],
            "1459成功保留必读且丢弃的u32尾，失败仅使用普通前缀");
        var quest = QuestEligibilityTests.Quest(400) with { Condition = "[quest clear]",SubType = 2,IntData = [401] };
        var quests = new QuestCatalog(new[] { quest,QuestEligibilityTests.Quest(401) }.ToDictionary(row => row.Id));
        var connections = Connections((400,1100),(401,0),(402,-1));
        var active = new ActiveMainQuest(0,401,1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1,0,"Replay",12,new Location(38,2,450,240),null)
        { Level = 20,CompletedQuests = [400],ActiveMainQuests = [active] };
        var start = new DungeonMap("map",new(0,0),2001,"map/replay-start.map");
        var boss = new DungeonMap("boss",new(1,0),2002,"map/replay-boss.map");
        var maze = new DungeonMaze(3,2,1,"BB EE",[start.Position],[boss.Position],[start,boss],
            [new(0,0),new(0,400),new(0,-1)]);
        var definition = new DungeonDefinition(1100,"dungeon/replay.dgn",1,20,[maze],[]);
        That(connections.TryGetReplayDungeon(400,out uint route) && route == 1100 &&
            !connections.TryGetReplayDungeon(401,out _) && !connections.TryGetReplayDungeon(402,out _),
            "回放路线使用sequence正参数，不用零/负值或任务目标猜副本");
        for (byte job = 0; job < 16; job++)
        {
            var source = character with { Job = job };
            var replay = QuestReplayPlan.Prepare(source,400,quests,connections);
            That(replay is not null && ReferenceEquals(replay.Expected,source) && replay.DungeonId == 1100,
                "已完成回放资格适用当前角色全部基础职业 " + job);
            That(DungeonEntryPlan.TryCreate(definition,source,1100,400,0,out var entry,out _,quest: quest,isReplay: true) &&
                entry is { IsQuestReplay: true,QuestId: 0,RouteQuestId: 400,ActiveQuestSlot: byte.MaxValue,QuestAcceptanceId: var acceptance } &&
                acceptance == Guid.Empty && entry.QuestAcceptances.IsEmpty && entry.Maze == maze &&
                source.CompletedQuests.SequenceEqual([400]) && source.ActiveMainQuests.SequenceEqual([active]) && source.Revision == 12,
                "回放选择原剧情迷宫，保留实际其他接取且不创建回放接取/完成/奖励 " + job);
        }
        That(QuestReplayPlan.Prepare(character with { CompletedQuests = [],ActiveMainQuests = [new(0,400,0)] },400,quests,connections) is null,
            "仅条件已达成或活动任务不能冒充已完成回放资格");
        That(QuestReplayPlan.Prepare(character with { CompletedQuests = [],AccountCompletedQuests = [400] },400,quests,connections) is not null,
            "已保存账号完成位与正常客户端完成投影保持一致");
        That(QuestReplayPlan.Prepare(character,0,quests,connections) is null && QuestReplayPlan.Prepare(character,30000,quests,connections) is null &&
            QuestReplayPlan.Prepare(character,400,quests,Connections((400,0))) is null &&
            QuestReplayPlan.Prepare(character,400,quests,Connections((400,1100),(400,1101))) is null,
            "未知编号、无显式路线及冲突路线不签发回放入口");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze with { QuestConnectionTokens = [new(0,1),new(0,400),new(0,-1)] }] },
            character,1100,400,0,out _,out _,quest: quest,isReplay: true),
            "回放原剧情路线不误选已完成connection或普通地图");
        That(DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,maze with { SourceOrdinal = 1 }] },character,1100,400,0,
            out var duplicate,out _,quest: quest,isReplay: true) && duplicate!.Maze.SourceOrdinal == maze.SourceOrdinal,
            "回放等价重复路线保留首个资源序号");
        That(!DungeonEntryPlan.TryCreate(definition with { Mazes = [maze,maze with { ClearConditions = [new("hunt monster",999,1)] }] },
            character,1100,400,0,out _,out _,quest: quest,isReplay: true) &&
            !DungeonEntryPlan.TryCreate(definition,character with { CompletedQuests = [] },1100,400,0,out _,out _,quest: quest,isReplay: true),
            "迷宫歧义和失去真实完成事实时拒绝回放，不借用普通接取绕过");
        var cell = start with { Position=new(0,0) };
        var single = maze with { Width=1,Height=1,Greed="AA",Starts=[cell.Position],Bosses=[cell.Position],MapSpecification=[cell] };
        var townDefinition = definition with { Mazes=[single] };
        That(DungeonEntryPlan.TryCreate(townDefinition,character,1100,400,0,out var promoted,out _,quest:quest,isReplay:true) &&
            promoted is not null && promoted.StartMap == promoted.BaseBossMap && promoted.BaseBossMap == promoted.BossMap && promoted.BossMap.Kind == "boss" &&
            promoted.Maze.MapSpecification!.Single() == promoted.BossMap && promoted.BossMap.Id == cell.Id && promoted.BossMap.Path == cell.Path &&
            single.MapSpecification!.Single().Kind == "map" && promoted.QuestId == 0 && promoted.RouteQuestId == 400 &&
            promoted.Maze.QuestReplayRoom is { QuestId:400,Map.Kind:"boss" } && promoted.Maze.StaticTopology is { Supported:true,Rooms.Count:1,Links.Count:0 },
            "显式单格回放map起终点同步投影，保留资源身份及原定义，不建立任务接取");
        var ordinary = character with { CompletedQuests=[],ActiveMainQuests=[new(0,400,1) { AcceptanceId=Guid.NewGuid() }] };
        That(!DungeonEntryPlan.TryCreate(townDefinition,ordinary,1100,400,0,out _,out var ordinaryReason,quest:quest) &&
            ordinaryReason == "quest-maze-map-unavailable","单格回放投影不放宽同资源的普通任务路线");
        var markedInvalid = promoted!.Maze with { QuestReplayRoom = promoted.Maze.QuestReplayRoom! with { Map = promoted.BossMap with { Id = 2003 } } };
        That(!markedInvalid.StaticTopology.Supported && markedInvalid.StaticTopology.UnsupportedReason == "任务回放单房资源身份或零出口合同不匹配",
            "回放单房身份与冻结地图不一致时不借用零出口豁免");
        foreach (var invalid in new[] {
            single with { Starts=[] },single with { Starts=[cell.Position,cell.Position] },
            single with { Bosses=[] },single with { Bosses=[cell.Position,cell.Position] },
            single with { Bosses=[new(1,0)] },single with { MapSpecification=[cell,cell with { Id=2003 }] },
            single with { Width=2,Greed="BB EE",MapSpecification=[cell,cell with { Position=new(1,0) }] },
            single with { LayeredMaps=[cell with { Kind="layered" }] },
            single with { ConditionalMaps=[cell with { IsConditional=true }] },
            single with { DummyStages=[cell with { DummyStageIndex=0 }] },
            single with { StoryStages=[cell with { StoryStageIndex=0 }] },
            single with { QuestConnectionTokens=[] },
            single with { QuestConnectionTokens=[new(0,0),new(0,401),new(0,-1)] } })
            That(!DungeonEntryPlan.TryCreate(townDefinition with { Mazes=[invalid] },character,1100,400,0,out _,out _,quest:quest,isReplay:true),
                "单格回放仍拒绝缺失/歧义/越界起终点、多格多阶段与无匹配显式连接");
        Console.WriteLine($"PASS {Count-checks} 合成任务回放资格、原剧情路线及状态隔离断言");
    }

    internal static QuestConnectionCatalog Connections(params (int Quest,int Dungeon)[] steps)
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture","[sequence]","[/sequence]","[/connect quest unit]"];
        var tokens = new List<PvfToken> { new(3,0),new(3,1),new(6,2),new(3,3),new(0,1),new(3,4),new(6,5),new(3,6) };
        foreach (var (quest,dungeon) in steps) { tokens.Add(new(0,quest)); tokens.Add(new(0,dungeon)); }
        tokens.Add(new(3,7)); tokens.Add(new(3,8));
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }
}
