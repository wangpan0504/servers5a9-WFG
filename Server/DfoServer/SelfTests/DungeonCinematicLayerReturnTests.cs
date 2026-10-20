using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonCinematicLayerReturnTests
{
    internal static void Run()
    {
        var strings = new List<string>();
        PvfToken Tag(string text) { strings.Add("["+text+"]"); return new(3,strings.Count-1); }
        string Resolve(int id) => strings[id];
        PvfToken[] action = [Tag("TRIGGER"),Tag("LIMIT"),new(0,1),Tag("DO BEHAVIOR"),Tag("ME"),new(0,0),
            Tag("/TRIGGER"),Tag("BEHAVIOR"),Tag("CINEMATIC"),new(0,917),Tag("/BEHAVIOR")];
        That(DungeonCinematicLayerReturnRules.OneShotCinematic(action,Resolve) == 917,"单次无条件MAP动作提供原CMT身份");
        foreach (var invalid in new[] { action[..^1],action.Concat(new[] { Tag("TRIGGER") }).ToArray(),
            action.Select((token,at) => at == 2 ? new PvfToken(0,2) : token).ToArray(),
            action.Select((token,at) => at == 3 ? Tag("ON INIT") : token).ToArray() })
            That(DungeonCinematicLayerReturnRules.OneShotCinematic(invalid,Resolve) is null,"条件或重复动作不授予无条件返回资格");
        PvfToken[] movie = [Tag("MAP"),new(0,53662),Tag("TYPE"),Tag("SCENARIO"),Tag("MOVE ALLOW"),new(0,0),
            Tag("SCENE"),Tag("ALLOW DAMAGE"),new(0,0),Tag("BEHAVIOR"),Tag("CHANGE MAP"),
            Tag("X MIN"),new(0,1000),Tag("X MAX"),new(0,1100),Tag("Y MIN"),new(0,200),Tag("Y MAX"),new(0,300),
            Tag("Z MIN"),new(0,0),Tag("Z MAX"),new(0,0),Tag("CHARACTER VIEW"),new(0,1),
            Tag("/CHANGE MAP"),Tag("/BEHAVIOR"),Tag("/SCENE")];
        That(DungeonCinematicLayerReturnRules.IsNonCombatMapChange(53662,movie,Resolve),"完整非战斗CMT声明限定同MAP返回");
        var hidden = movie.ToArray(); hidden[24] = new(0,0);
        That(DungeonCinematicLayerReturnRules.IsNonCombatMapChange(53662,hidden,Resolve),"CharacterView两值均为同一导航类别");
        foreach (var invalid in new[] { movie[..^1],movie.Concat(movie).ToArray(),
            Change(1,53654),Change(5,1),Change(8,1),Change(14,999),Change(24,2),
            movie.Concat(new[] { Tag("CHANGE MAP") }).ToArray() })
            That(!DungeonCinematicLayerReturnRules.IsNonCombatMapChange(53662,invalid,Resolve),"异图、可战斗、越界或不完整演出不放行");
        var nested = movie.Take(10).Concat(new[] { Tag("ACTOR") }).Concat(movie.Skip(10).Take(16))
            .Concat(new[] { Tag("/ACTOR") }).Concat(movie.Skip(26)).ToArray();
        That(!DungeonCinematicLayerReturnRules.IsNonCombatMapChange(53662,nested,Resolve),"嵌套actor命令不能冒充独立CHANGE MAP行为");
        PvfToken[] Change(int at,int value) { var copy = movie.ToArray(); copy[at] = new(0,value); return copy; }

        strings.Add("none.ai");
        var noAi = new PvfToken(6,strings.Count-1);
        PvfToken[] actorAction = [Tag("TRIGGER"),Tag("ON START MAP"),Tag("WHICH"),Tag("AI CHARACTER"),Tag("IS INDEX"),new(0,2000),
            Tag("/IS INDEX"),Tag("DO BEHAVIOR"),Tag("CHECKUP OBJECT"),new(0,1),Tag("DO BEHAVIOR"),Tag("ME"),new(0,0),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("CINEMATIC"),new(0,2405),Tag("/BEHAVIOR"),Tag("BEHAVIOR"),
            Tag("CHANGE AI"),Tag("NORMAL"),new(0,0),noAi,Tag("CHANGE AI"),Tag("NORMAL"),new(0,1),noAi,
            Tag("CHANGE AI"),Tag("NORMAL"),new(0,2),noAi,Tag("CHANGE AI"),Tag("NORMAL"),new(0,3),noAi,Tag("/BEHAVIOR")];
        That(DungeonCinematicLayerReturnRules.ActorCinematic(actorAction,Resolve) == (2000,2405),"完整APC触发声明保留演员模板及演出身份");
        foreach (var invalid in new[] { actorAction[..^1],actorAction.Concat(action).ToArray(),
            actorAction.Select((token,at) => at == 9 ? new PvfToken(0,0) : token).ToArray(),
            actorAction.Select((token,at) => at == 25 ? new PvfToken(0,0) : token).ToArray() })
            That(DungeonCinematicLayerReturnRules.ActorCinematic(invalid,Resolve) is null,"条件或AI行为不完整、重复时不解释为APC返回来源");
        PvfToken[] positionActor = [Tag("ACTOR"),Tag("TYPE"),Tag("POS"),Tag("INDEX"),new(0,0),Tag("POS"),new(0,0),new(0,0),Tag("/ACTOR")];
        var actorMovie = movie.Take(10).Concat(positionActor).Concat(movie.Skip(10)).ToArray();
        actorMovie[8] = new(0,1);
        That(DungeonCinematicLayerReturnRules.IsActorMapChange(53662,actorMovie,Resolve) &&
            !DungeonCinematicLayerReturnRules.IsNonCombatMapChange(53662,actorMovie,Resolve),
            "APC演出的末段导航允许布尔伤害场，原非战斗谓词保持拒绝");
        foreach (var invalid in new[] { actorMovie[..^1],actorMovie.Concat(actorMovie).ToArray(),
            actorMovie.Select((token,at) => at == 8 ? new PvfToken(0,2) : token).ToArray(),
            actorMovie.Select((token,at) => at == 12 ? Tag("CHARACTER") : token).ToArray(),
            actorMovie.Select((token,at) => at == 1 ? new PvfToken(0,53654) : token).ToArray() })
            That(!DungeonCinematicLayerReturnRules.IsActorMapChange(53662,invalid,Resolve),"异图、非布尔伤害、错误导航演员或非末段行为不授予APC返回");

        var start = new DungeonMap("map",new(0,0),100,"map/base.map");
        var boss = new DungeonMap("boss",new(1,0),53654,"map/boss.map");
        var layer = boss with { Kind="layered",Id=53662,Path="map/layer.map" };
        var maze = new DungeonMaze(0,2,1,"BBEE",[start.Position],[boss.Position],[start,boss],[]) { LayeredMaps=[layer] };
        var accepted = new ActiveMainQuest(0,3444,1) { AcceptanceId=Guid.NewGuid() };
        var entry = new DungeonEntryPlan(99,0,148,3444,0,0,maze,start,boss)
        { HasExplicitClearMap=true,QuestAcceptanceId=accepted.AcceptanceId,QuestAcceptances=[accepted] };
        var evidence = new DungeonCinematicLayerReturn(layer.Id,layer.Path,"map/Action/53662.act",917,"cinematic/917.cmt");
        foreach (uint request in new uint[] { 3444,0 })
        {
            var selected = request == 0 ? entry with { QuestId=0,ActiveQuestSlot=255,QuestAcceptanceId=Guid.Empty } : entry;
            var (run,authority) = Create(selected,evidence);
            var old = run.Snapshot.Identity;
            That(run.MoveLayer(old,Guid.NewGuid(),boss.Position,false).Outcome == DungeonRunOutcome.Rejected,
                "未清演出层kind0不能借用原生kind1返回资格");
            var pending = run.PrepareConfirmedDeath(authority.Death(old,30));
            That(pending.Pending is not null && run.MoveLayer(old,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "未提交死亡仍阻止演出导航");
            run.CancelUncommittedDeath(pending.Pending!);
            Guid operation = Guid.NewGuid();
            var result = run.MoveLayer(old,operation,boss.Position,true);
            That(result.Outcome == DungeonRunOutcome.Accepted && result.ClearFact is null && result.CulledMonsterKeys.Count == 0 &&
                result.Snapshot is { MapOperation:DungeonMapOperation.RestoreBase,MapPayload:DungeonMapPayload.Cached } &&
                result.Snapshot.CurrentMap == boss && result.Snapshot.LivingMonsters.SequenceEqual(new ushort[] { 20 }) &&
                run.ClearFact is null,"显式及普通0入口恢复原Boss缓存，不杀演员、不签发清图");
            That(run.MoveLayer(old,operation,boss.Position,true).Outcome == DungeonRunOutcome.Duplicate &&
                run.MoveLayer(old,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "返回重放只去重，旧epoch不能作为新操作");
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "演出层不能重新进入并恢复一份新Boss");
            var death = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
            That(death.ClearFact?.ClearMap == boss && death.ClearFact.ConfirmedDeathEvents.Count == 1,
                "只由恢复后真实基础Boss死亡生成原终点的唯一死亡凭据");
        }
        foreach (var invalid in new[] { entry with { QuestAcceptances=[] },entry with { HasExplicitClearMap=false },
            entry with { IsQuestReplay=true },entry with { QuestAcceptances=[accepted with { AcceptanceId=Guid.Empty }] },
            entry with { QuestAcceptances=[accepted,accepted with { Slot=1 }] },entry with { QuestAcceptanceId=Guid.NewGuid() } })
        {
            var (run,_) = Create(invalid,evidence);
            That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "缺失或歧义原接取、回放及非显式终点不能借用演出返回");
        }
        foreach (var invalid in new DungeonCinematicLayerReturn?[] { null,evidence with { MapId=53654 },evidence with { MapPath="map/other.map" },
            evidence with { ActorId=2000 } })
        {
            var (run,_) = Create(entry,invalid);
            That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "普通或错绑叠层保留真实清怪门禁");
        }

        var actorEntry = entry with { HasExplicitClearMap=false,Maze=maze with
            { QuestConnectionTokens=[new(0,0),new(0,3444),new(0,-1)] } };
        var actorEvidence = evidence with { ActorId=2000 };
        foreach (bool replay in new[] { false,true })
        {
            var selected = replay ? actorEntry with { IsQuestReplay=true,QuestId=0,ActiveQuestSlot=255,QuestAcceptanceId=Guid.Empty } : actorEntry;
            var (run,_) = Create(selected,actorEvidence,actorRoom:true);
            var result = run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true);
            That(result.Outcome == DungeonRunOutcome.Accepted && result.ClearFact is null && result.CulledMonsterKeys.Count == 0 &&
                result.Snapshot.CurrentMap == boss,"APC任务连接与正式回放可导航，不冒充clear-map任务或签发死亡");
        }
        foreach (var invalid in new[] { actorEntry with { Maze=maze },actorEntry with { RouteQuestId=3445 },
            actorEntry with { QuestAcceptances=[] },actorEntry with { IsQuestReplay=true } })
        {
            var (run,_) = Create(invalid,actorEvidence,actorRoom:true);
            That(run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true).Outcome == DungeonRunOutcome.Rejected,
                "APC来源不能借用缺失任务连接、异路线、缺接取或错误回放实例身份");
        }

        (SinglePlayerDungeonRun Run,Authority Authority) Create(DungeonEntryPlan selected,DungeonCinematicLayerReturn? movieEvidence,bool actorRoom = false)
        {
            DungeonMonsterPlan Plan(DungeonMap map,ushort key) => DungeonMonsterPlan.FromRoom(new(map,
                [new(1,[1,0,20,0,0,0,1,1],"[fixed]","[boss]","monster/fixture.mob")],[],[])
                {
                    CinematicLayerReturn=map == layer ? movieEvidence : null,
                    AiCharacters=actorRoom && map == layer ? [new(2000,[2000,177,2144,0,0,0],"[character]","[normal]","aicharacter/fixture.chr",20)] : []
                },99,key);
            var plans = new[] { DungeonMonsterPlan.FromRoom(new(start,[],[],[]),99,10),Plan(boss,20),Plan(layer,30) };
            var authority = new Authority();
            var approved = new DungeonFixedRoute(plans,[new(start.Position,boss.Position)]);
            if (actorRoom && selected.IsQuestReplay && selected.QuestId == 0)
                That(!SinglePlayerDungeonRun.TryCreate(selected with { QuestAcceptanceId=Guid.NewGuid() },1,100,approved,authority,out _,out string invalidReason) &&
                    invalidReason == "invalid: 实例身份或任务尝试无效","带伪造接取的回放在Run准入即拒绝，不能假定已经进入演出层");
            That(SinglePlayerDungeonRun.TryCreate(selected,1,100,approved,authority,out var run,out _),
                "演出导航用例通过正式Run准入，不另建剧情状态");
            run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position);
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),boss.Position,true);
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            That(run.Snapshot.CurrentMap == layer && run.Snapshot.LivingMonsters.SequenceEqual(actorRoom ? new ushort[] {30,31} : [30]),
                "进入仍有真实演员的末层");
            return (run,authority);
        }
    }

    sealed class Authority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,++sequence);
    }
}
