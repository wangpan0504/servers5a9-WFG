using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonScriptedLayerReturnTests
{
    internal static void Run()
    {
        var strings = new List<string>();
        PvfToken Tag(string value) { strings.Add("["+value+"]"); return new(3,strings.Count-1); }
        string Resolve(int id) => strings[id];
        PvfToken[] action = [Tag("TRIGGER"),Tag("FRAME"),new(0,15),new(0,15),Tag("/TRIGGER"),
            Tag("BEHAVIOR"),Tag("MOVE MAP"),new(0,1),new(0,0),new(0,406),new(0,209),new(0,1),Tag("/MOVE MAP"),Tag("/BEHAVIOR")];
        That(DungeonScriptedLayerReturnRules.ReturnMove(action,Resolve) == (1,0,406,209,1),"ACT返回保留完整五整数，不把末参数当wire kind");
        foreach (var invalid in new[] { action[..^1],action.Concat(action).ToArray(),
            action.Select((token,at) => at == 11 ? new PvfToken(0,2) : token).ToArray(),
            action.Take(5).Concat(new[] { Tag("ACTOR") }).Concat(action.Skip(5)).Concat(new[] { Tag("/ACTOR") }).ToArray() })
            That(DungeonScriptedLayerReturnRules.ReturnMove(invalid,Resolve) is null,"不完整、重复、未知参数或嵌套行为不能授权导航");

        var start = new DungeonMap("map",new(0,0),59911,"map/start.map");
        var source = new DungeonMap("map",new(1,0),59913,"map/source.map");
        var boss = new DungeonMap("boss",new(2,0),59914,"map/terminal.map");
        var layer = boss with { Kind="layered",Position=source.Position };
        var maze = new DungeonMaze(0,3,1,"BBEE",[start.Position],[boss.Position],[start,source,boss],[]) { LayeredMaps=[layer] };
        var accepted = new ActiveMainQuest(0,6032,1) { AcceptanceId=Guid.NewGuid() };
        var entry = new DungeonEntryPlan(99,0,291100319,6032,0,0,maze,start,boss)
        {
            HasExplicitClearMap=true,QuestAcceptanceId=accepted.AcceptanceId,QuestAcceptances=[accepted],
            QuestClearMap=layer,TerminalLayerMap=layer,TerminalLayerQuestId=6032,TerminalLayerQuestSlot=0,
            TerminalLayerAcceptanceId=accepted.AcceptanceId
        };
        var evidence = new DungeonScriptedLayerReturn(layer.Id,layer.Path,0,109000009,"passiveobject/event.obj",0,
            "passiveobject/action/6032_3.act",1,0,406,209,1);
        foreach (bool ordinary in new[] { false,true })
        foreach (int order in new[] { 0,1,2 })
        {
            var selected = ordinary ? entry with { QuestId=0,ActiveQuestSlot=255,QuestAcceptanceId=Guid.Empty } : entry;
            var (run,authority) = Create(selected,evidence);
            var before = run.Snapshot;
            int creaturePoints = run.CreatureExperiencePoints;
            if (order == 2) run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,30));
            var clear = run.ClearFact;
            var result = run.BeginScriptedReturn(run.Snapshot.Identity,source.Position,0);
            That(result is { Outcome:DungeonRunOutcome.Accepted,Snapshot.MapOperation:DungeonMapOperation.RestoreBase,
                Snapshot.MapPayload:DungeonMapPayload.Cached } && run.Snapshot.CurrentMap == layer && ReferenceEquals(clear,run.ClearFact),
                "清图前后均只授权缓存基础呈现，保留末层ClearFact和地图");
            run.ConfirmScriptedReturnLoading(run.Snapshot.Identity,run.ScriptedReturnId);
            That(run.CompleteScriptedReturn(run.Snapshot.Identity) == DungeonRunOutcome.Rejected &&
                run.BeginScriptedReturn(run.Snapshot.Identity,source.Position,0)?.Outcome == DungeonRunOutcome.Accepted,
                "尚未成功发布缓存地图时保留同许可重试，不提前确认加载");
            run.ConfirmScriptedReturnPresentation(run.Snapshot.Identity,run.ScriptedReturnId);
            if (order == 0)
            {
                That(run.CompleteScriptedReturn(run.Snapshot.Identity) == DungeonRunOutcome.Accepted,"死亡前呈现加载确认");
                run.ConfirmScriptedReturnLoading(run.Snapshot.Identity,run.ScriptedReturnId);
            }
            if (order != 2) run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,30));
            if (order != 0) That(run.CompleteScriptedReturn(run.Snapshot.Identity) == DungeonRunOutcome.Accepted,"死亡后原呈现许可继续有效");
            run.ConfirmScriptedReturnLoading(run.Snapshot.Identity,run.ScriptedReturnId);
            var fact = run.ClearFact;
            That(run.Phase == SinglePlayerDungeonPhase.Settlement && fact?.ClearMap == layer &&
                fact.ConfirmedDeathEvents.Count == 1 && run.Snapshot.Identity.Epoch == before.Identity.Epoch+1,
                "只有可信末层死亡推进epoch和结算，不因导航重建事实");
            That(creaturePoints == 3 && run.CreatureExperiencePoints == creaturePoints,
                "呈现45/37不重复加载房间或增加宠物成长点");
            var saved = run.Snapshot.Identity;
            That(run.BeginScriptedReturn(saved,source.Position,0)?.Outcome == DungeonRunOutcome.Duplicate &&
                run.CompleteScriptedReturn(saved) == DungeonRunOutcome.Duplicate && run.ConfirmScriptedReturnBase(saved,source.Position) &&
                run.ConfirmScriptedReturnBase(saved,source.Position) && run.Snapshot.Identity == saved && ReferenceEquals(fact,run.ClearFact),
                "重复返回、37和kind1确认不推进实例或重建清图");
            That(run.MoveLayer(saved,Guid.NewGuid(),source.Position,true).Outcome == DungeonRunOutcome.Rejected &&
                run.BeginScriptedReturn(saved,boss.Position,0)?.Outcome == DungeonRunOutcome.Rejected,
                "呈现返回后不再进入真实换层或授权其他坐标");
            var reward = new DungeonClearExperienceReward(90,10,100,1,"fixture-clear","fixture/clear.tbl");
            var settlement = run.PrepareSettlement(fact!,new byte[] { 1 },reward);
            That(settlement.Pending?.MapId == layer.Id && run.BeginScriptedReturn(saved,source.Position,0) is null &&
                run.CompleteScriptedReturn(saved) == DungeonRunOutcome.Rejected,"结算使用原末层，未决事务阻止导航确认");
        }
        foreach (var invalid in new[] { entry with { QuestAcceptances=[] },entry with { IsQuestReplay=true },
            entry with { HasExplicitClearMap=false },entry with { TerminalLayerAcceptanceId=Guid.NewGuid() } })
        {
            That(!SinglePlayerDungeonRun.TryCreate(invalid,1,100,Route(evidence),new Authority(),out _,out _),
                "不匹配的原接取和路线在构造时拒绝，不能借导航资格");
        }
        foreach (var invalid in new DungeonScriptedLayerReturn?[] { null,evidence with { ObjectId=10 },
            evidence with { MapPath="map/wrong.map" },evidence with { SceneFlag=0 } })
        {
            var (run,_) = Create(entry,invalid);
            That(run.BeginScriptedReturn(run.Snapshot.Identity,source.Position,0) is null,"缺失或错绑资源声明保留原清怪守卫");
        }
        {
            var (run,authority) = Create(entry,evidence);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,30));
            That(pending.Pending is not null && run.BeginScriptedReturn(run.Snapshot.Identity,source.Position,0) is null,"死亡提交未明时不先返回成功");
            run.CancelUncommittedDeath(pending.Pending!);
            That(run.BeginScriptedReturn(run.Snapshot.Identity,source.Position,0)?.Outcome == DungeonRunOutcome.Accepted,"死亡回滚后原实例仍可合法导航");
        }

        DungeonFixedRoute Route(DungeonScriptedLayerReturn? declaration)
        {
            DungeonMonsterPlan Plan(DungeonMap map,ushort key) => DungeonMonsterPlan.FromRoom(new(map,
                map == layer || map == boss ? [new(1,[1,0,20,0,0,0,1,1],"[fixed]","[boss]","monster/fixture.mob")] : [],
                map == layer ? [new(109000009,[109000009,0,0,0],"passiveobject/event.obj")] : [],[])
                { DeclaredType=map == layer || map == boss ? "[boss]" : "[normal]",
                    ScriptedLayerReturn=map == layer ? declaration : null },99,key);
            var plans = new[] { Plan(start,10),Plan(source,15),Plan(boss,20),Plan(layer,30) };
            return new(plans,[new(start.Position,source.Position),new(source.Position,boss.Position)]);
        }

        (SinglePlayerDungeonRun Run,Authority Authority) Create(DungeonEntryPlan selected,DungeonScriptedLayerReturn? declaration)
        {
            var authority = new Authority();
            That(SinglePlayerDungeonRun.TryCreate(selected,1,100,Route(declaration),
                authority,out var run,out _),"导航用例沿正式Run建立显式末层");
            run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            run.Move(run.Snapshot.Identity,Guid.NewGuid(),source.Position);
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            run.MoveLayer(run.Snapshot.Identity,Guid.NewGuid(),source.Position,true);
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
            That(run.Snapshot.CurrentMap == layer && run.Phase == SinglePlayerDungeonPhase.Dungeon,"未访问基础Boss便进入未清末层");
            return (run,authority);
        }
    }

    sealed class Authority : DungeonCombatAuthority
    {
        long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,++sequence);
    }
}
