using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassDazzlementClearCore()
    {
        int before=Count;
        var at=new DateTimeOffset(2026,11,1,7,0,0,TimeSpan.FromHours(8));
        var clock=new DazzlementClearClock(at);
        var definition=new DungeonDefinition(7601,"dungeon/towers/towerofdazzlement/core.dgn",null,null,[],[])
            { DungeonType="tower of dazzlement" };
        var role=new Character(1,0,"DazzleCore",0,Location,Location);
        var (run,authority)=CreateRun(role,entry=>entry with
            { DungeonId=7601,QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0,Difficulty=0 });
        var admission=DazzlementTowerAdmission.At(at);
        That(BattlePassDazzlementClear.Prepare(definition,run.Entry,
            new(run.Snapshot.Identity,7601,[]),admission,clock) is null,"仅真实入场无原终末清图不建整次来源");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
        var pending=run.PrepareSettlement(run.ClearFact!,[1,2,3],Reward).Pending!;
        pending.FreezeBattlePassDazzlementClear(definition,run.Entry,admission,clock);
        var source=pending.BattlePassDazzlementClear!;
        That(source.Identity==run.Snapshot.Identity && source.Admission==admission && source.PlannedAt==at &&
            source.SourceId.Length==32 && source.Matches(run,pending),"真实终末清图与原同实例眩惑资格冻结32hex整次身份");
        clock.Now=at.AddMinutes(1);pending.FreezeBattlePassDazzlementClear(definition,run.Entry,admission with { Day=admission.Day+1 },clock);
        That(ReferenceEquals(source,pending.BattlePassDazzlementClear) && source.PlannedAt==at && source.Admission==admission,
            "同原冻结计划重试不重采日资格或事件时间");
        var repeated=BattlePassDazzlementClear.Prepare(definition,run.Entry,run.ClearFact!,admission,clock)!;
        That(source.SourceId==repeated.SourceId,"同完整来源身份确定性恢复");
        var another=BattlePassDazzlementClear.Prepare(definition,run.Entry,run.ClearFact! with
            { Identity=run.ClearFact!.Identity with { InstanceId=Guid.NewGuid() } },admission,clock)!;
        That(source.SourceId!=another.SourceId,"不同正式实例不误去重");
        var copied=new PreparedDungeonSettlement(run.ClearFact! with { },[1,2,3],Reward,pending.MapId);
        That(!source.Matches(run,copied),"复制事实不得改绑原不可变计划");
        var mission=new BattlePassMission(16,1,100,45,"fixture-dazzlement",-1);
        That(BattlePassDazzlementClear.MatchesMission(0,mission),"原日16/raw45/1/100/-1精确匹配");
        foreach(byte category in new byte[]{1,2,255})That(!BattlePassDazzlementClear.MatchesMission(category,mission),"不扩原类别");
        foreach(var wrong in new[]{mission with { Id=17 },mission with { RawTypeCode=7 },mission with { RawParameter=1 },
            mission with { RequiredCount=2 },mission with { Experience=101 }})
            That(!BattlePassDazzlementClear.MatchesMission(0,wrong),"不改原定义数量经验参数");
        foreach(var excluded in new[]{run.Entry with { IsPractice=true },run.Entry with { IsTutorial=true },
            run.Entry with { IsQuestReplay=true },run.Entry with { DisableClearReward=true },
            run.Entry with { RaidEntry=new RaidDungeonIdentity(Guid.NewGuid(),1,0) },run.Entry with { Difficulty=1 },
            run.Entry with { RouteQuestId=3145 }})
            That(BattlePassDazzlementClear.Prepare(definition,excluded,run.ClearFact!,admission,clock) is null,
                "原练习教程重放禁奖攻坚非原难度剧情入口不借眩惑来源");
        That(BattlePassDazzlementClear.Prepare(definition,run.Entry,run.ClearFact!,null,clock) is null &&
            BattlePassDazzlementClear.Prepare(definition with { DungeonType=null },run.Entry,run.ClearFact!,admission,clock) is null &&
            BattlePassDazzlementClear.Prepare(definition with { Path="dungeon/other.dgn" },run.Entry,run.ClearFact!,admission,clock) is null,
            "缺原眩惑入场资格资源类型或资源路径不建来源");
        That(BattlePassDazzlementClear.Prepare(definition,run.Entry,run.ClearFact! with { ClearMap=null },admission,clock) is null &&
            BattlePassDazzlementClear.Prepare(definition,run.Entry,run.ClearFact! with { ConfirmedDeathEvents=[] },admission,clock) is null,
            "非终末房或无原权威确认死亡不建来源");
        Console.WriteLine($"PASS BattlePass Dazzlement completion core ({Count-before} checks)");
    }
    sealed class DazzlementClearClock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
