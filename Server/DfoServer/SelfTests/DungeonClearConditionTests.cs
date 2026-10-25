using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonClearConditionTests
{
    public static void Run()
    {
        int before = Count;
        var raw = new DungeonRawSection[] { new("clear condition",[]),new("hunt monster",[new(0,501),new(0,1)]),
            new("/clear condition",[]) };
        var conditions = DungeonMazeConditions.Clear(raw,_ => "unused");
        That(conditions.SequenceEqual(new[] { new DungeonClearCondition("hunt monster",501,1) }),"通关猎杀目标按原声明读取");
        That(DungeonMazeConditions.Clear([raw[0],raw[1],new("clear map",[new(0,100),new(0,101)]),raw[2]],_ => "unused").Count == 2,
            "未知混合声明不能丢弃后作为唯一猎杀条件");
        Reject<InvalidDataException>(() => DungeonMazeConditions.Clear([raw[0],new("hunt monster",[new(0,501)]),raw[2]],_ => "unused"),
            "残缺目标数量声明保留资源错误");
        That(DungeonMazeConditions.Clear([raw[0],new("limit time",[new(0,600),new(0,1200)]),raw[2]],_ => "unused") is
            [{ Type: "limit time", TargetId: 600, Count: 1200 }], "限时两个原始标量分别保留，不置零或解释成普通猎杀目标");
        foreach (var invalid in new PvfLib.PvfToken[][] { [new(0,600)], [new(0,600),new(0,0)],
            [new(0,-1),new(0,1200)], [new(0,600),new(7,1200)], [new(0,600),new(0,1200),new(0,1800)] })
            Reject<InvalidDataException>(() => DungeonMazeConditions.Clear([raw[0],new("limit time",invalid),raw[2]],_ => "unused"),
                "限时条件不接受缺项、额外项、错误类型或无效数值");

        var (run,authority,boss) = Create(conditions,false);
        var identity = run.Snapshot.Identity;
        That(run.Move(identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Rejected,"条件目标死亡前仍按实际房间阻挡敌判断过房");
        var death = authority.Death(identity,10);
        var prepared = run.PrepareConfirmedDeath(death).Pending!;
        That(prepared.Result.ClearFact is null && prepared.Result.CulledMonsterKeys.SequenceEqual(new ushort[] { 11 }) &&
            run.Snapshot.LivingMonsters.Count == 2 && !prepared.Monster.IsBoss && prepared.Monster.Source!.RankKind == "[normal]",
            "非终点条件规划只清本房，不改目标品级，数据库提交前不改变死亡集合");
        run.ConfirmCommittedDeath(prepared);
        That(run.AcceptConfirmedDeath(death).Outcome == DungeonRunOutcome.Duplicate && run.Snapshot.LivingMonsters.Count == 0,
            "条件清房保留真实死亡身份，重复报告不重复清退");
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Accepted,
            "非终点清房继续原路线加载而不直接结算");

        (run,authority,_) = Create(conditions,true);
        prepared = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,10)).Pending!;
        That(prepared.Result.ClearFact is { ConfirmedDeathEvents.Count: 1 } && prepared.Result.CulledMonsterKeys.SequenceEqual(new ushort[] { 11,12 }),
            "终点指定普通目标死亡可清退剩余敌人与Boss，通关仅记录真实目标事件");
        run.ConfirmCommittedDeath(prepared);
        That(run.Phase == SinglePlayerDungeonPhase.Settlement && run.ClearFact?.ClearMap == run.Snapshot.CurrentMap,
            "明确猎杀条件复用原通关事实和结算阶段");

        foreach (var unsupported in new DungeonClearCondition[][] { [new("hunt monster",501,2)], [new("hunt monster",502,1)],
            [new("hunt boss",501,1)], [new("hunt apc",501,1)], [new("destroy object",501,1)],
            [new("hunt monster",501,1),new("hunt monster",502,1)] })
        {
            (run,authority,_) = Create(unsupported,true);
            prepared = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,10)).Pending!;
            That(prepared.Result.ClearFact is null && prepared.Result.CulledMonsterKeys.Count == 0,
                "非目标、普通rank冒充Boss及未确认计数/类型不触发附加清场");
            run.ConfirmCommittedDeath(prepared);
            That(run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,12)).ClearFact is not null,
                "未接入的附加条件不会阻断原真实Boss通关");
        }
        foreach (bool apcBoss in new[] { false,true })
        {
            (run,authority,_) = Create(conditions,true,apcBoss ? 1 : 2,apcBoss);
            var result = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,10));
            That(result.ClearFact is null && result.CulledMonsterKeys.Count == 0,
                "指定普通目标不跳过多Boss或敌对Boss APC的独立死亡");
            result = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,12));
            That(result.ClearFact is null,"剩余Boss尚存时继续等待真实死亡");
            result = run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,13));
            That(result.ClearFact is { ConfirmedDeathEvents.Count: 3 },"最后真实Boss死亡后按原流程结算，保留全部真实死亡事件");
        }
        Console.WriteLine($"PASS {Count-before} 显式单目标通关条件断言（合成资源）");
    }

    static (SinglePlayerDungeonRun Run,Authority Authority,DungeonMap Boss) Create(IReadOnlyList<DungeonClearCondition> conditions,bool terminal,
        int bossCount = 1,bool apcBoss = false)
    {
        var start = new DungeonMap(terminal ? "boss" : "map",new(0,0),100,"map/clear-start.map");
        var boss = terminal ? start : new DungeonMap("boss",new(1,0),101,"map/clear-boss.map");
        var maze = new DungeonMaze(0,terminal ? 1 : 2,1,terminal ? "BB" : "BBEE",[start.Position],[boss.Position],
            terminal ? [start] : [start,boss],[]) { ClearConditions = conditions };
        DungeonRoomMonster Monster(int id,string rank) => new(id,[id,0,20,0,0,0,1,1],"[fixed]",rank,"monster/fixture.mob");
        var room = new DungeonRoomDefinition(start,[Monster(501,"[normal]"),Monster(502,"[normal]"),
            .. terminal ? Enumerable.Range(503,bossCount).Select(id => Monster(id,"[boss]")) : []],[],[])
            { AiCharacters = apcBoss ? [new(600,[600,0,0,0,0,0],"[monster]","[boss]","aicharacter/fixture.aic",20)] : [] };
        var plans = new List<DungeonMonsterPlan> { DungeonMonsterPlan.FromRoom(room,99,10) };
        if (!terminal) plans.Add(DungeonMonsterPlan.FromRoom(new(boss,[Monster(503,"[boss]")],[],[]),99,20));
        var entry = new DungeonEntryPlan(99,0,777,0,byte.MaxValue,0,maze,start,boss);
        var authority = new Authority();
        That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new(plans,terminal ? [] : [new(start.Position,boss.Position)]),authority,out var run,out _),
            "条件清房沿完整资源与唯一对象key建立正式Run");
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        return (run,authority,boss);
    }

    sealed class Authority : DungeonCombatAuthority
    {
        private long sequence;
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,++sequence);
    }
}
