using DfoServer.Game.BattlePass;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassRaidCompletionTests
{
    internal static void Run(AntonRaidBattle.LukePhaseSettlement candidate,LukePhaseCommit committed)
    {
        int before=Count;var receipt=committed.Members[0];
        if(!candidate.FinalPhase)
            That(BattlePassRaidCompletion.Prepare(candidate,receipt with {CountsAsClear=true}) is null,"原P1即使伪填计次也不冒最终整场");
        else
        {
            var actual=receipt with {CountsAsClear=true,Eligible=false};
            var fact=BattlePassRaidCompletion.Prepare(candidate,actual)!;
            That(fact.Account==candidate.Members[0].Account && fact.Character==actual.Character && fact.RaidType==candidate.RaidType && fact.At==candidate.Anchor &&
                fact.SourceId.Length==32 && fact.SourceId.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),"原最终计次保成员账号/角色/冻结时间与32hex，不依赖领奖Eligible");
            That(fact.SourceId==BattlePassRaidCompletion.Prepare(candidate,actual)!.SourceId,"同原最终候选重试身份不变");
            That(BattlePassRaidCompletion.Prepare(candidate,actual with {CountsAsClear=false}) is null,"原不计次成员不能注册来源");
            That(fact.SourceId!=BattlePassRaidCompletion.Prepare(candidate,committed.Members[1] with {CountsAsClear=true})!.SourceId,"原同团不同成员不误合并来源");
            foreach(var wrong in new[]{actual with {Instance=Guid.NewGuid()},actual with {Event=Guid.NewGuid()},actual with {Phase=actual.Phase^1},
                actual with {RaidType=RaidKind.Anton},actual with {Character=ushort.MaxValue},actual with {Anchor=actual.Anchor+1},
                actual with {Week=actual.Week+1},actual with {Source="wrong"}})
                Reject<InvalidDataException>(()=>BattlePassRaidCompletion.Prepare(candidate,wrong),"原最终回执实例/事件/阶段/成员/时间/周/来源不可换绑");
        }
        foreach(var (kind,category,mission) in new (RaidKind Kind,byte Category,BattlePassMission Mission)[]
        {
            (RaidKind.Luke,1,new(46,3,1000,32,"fixture-luke-completion",-1)),
            (RaidKind.Fiendwar,2,new(58,20,1600,33,"fixture-fiendwar-completion",-1)),
            (RaidKind.Prey,2,new(59,20,1600,34,"fixture-prey-completion",-1))
        })
        {
            That(BattlePassRaidCompletion.MatchesMission(kind,category,mission),"只匹配本类攻坚原任务完整定义");
            foreach(var other in new[]{RaidKind.Anton,RaidKind.Luke,RaidKind.Fiendwar,RaidKind.Prey,(RaidKind)255}.Where(value=>value!=kind))
                That(!BattlePassRaidCompletion.MatchesMission(other,category,mission),"不同攻坚类型不互串计数");
            foreach(byte wrongCategory in new byte[]{0,1,2,255}.Where(value=>value!=category))
                That(!BattlePassRaidCompletion.MatchesMission(kind,wrongCategory,mission),"不扩原任务类别");
            foreach(var wrong in new[]{mission with {Id=0},mission with {RawTypeCode=0},mission with {RawParameter=1},
                mission with {RequiredCount=mission.RequiredCount+1},mission with {Experience=mission.Experience+1}})
                That(!BattlePassRaidCompletion.MatchesMission(kind,category,wrong),"不改原定义/数量/经验/参数");
        }
        Console.WriteLine($"PASS BattlePass raw32/33/34 original raid completion core ({Count-before} checks)");
    }
}
