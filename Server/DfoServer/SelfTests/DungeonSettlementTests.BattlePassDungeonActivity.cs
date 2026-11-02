using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassDungeonActivityCore()
    {
        int before=Count;
        var at=new DateTimeOffset(2026,11,1,7,0,0,TimeSpan.FromHours(8));
        var clock=new FormalClearClock(at);
        var role=new Character(1,0,"DeathCore",0,Location,Location);
        var(run,authority)=CreateRun(role,e=>e with { DungeonId=3,QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0 });
        var pending=run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,20)).Pending!;
        pending.FreezeBattlePassActivity(run,clock);
        var fact=pending.BattlePassActivity!;
        That(fact.MatchesDeath(run,pending) && fact.Amount==1 && !fact.IsBoss && fact.PlannedAt==at,
            "原实例普通敌方权威待提交死亡冻结typed事实");
        That(fact.SourceId.Length == 32 && fact.SourceId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "原死亡来源满足现有表32hex合同");
        That(BattlePassDungeonActivity.PrepareDeath(run,pending,clock)!.SourceId == fact.SourceId,
            "同一原死亡业务身份重复规划摘要稳定");
        clock.Now=at.AddSeconds(2);pending.FreezeBattlePassActivity(run,clock);
        That(ReferenceEquals(fact,pending.BattlePassActivity) && fact.PlannedAt==at,"原待提交重试保冻结事实及时间");
        That(fact.MatchesMission(0,new(1,10,100,1,"",-1)) && !fact.MatchesMission(0,new(2,2,100,2,"",-1)),
            "普通怪计击杀而不冒领主");
        foreach(var mutation in new[]{new BattlePassMission(1,11,100,1,"",-1),new(1,10,101,1,"",-1),
            new(1,10,100,1,"",0),new(1,10,100,2,"",-1)})
            That(!fact.MatchesMission(0,mutation),"原任务目标XP参数raw变异不能借用死亡");
        That(!fact.MatchesMission(1,new(1,10,100,1,"",-1)),"非每日类别不匹配");
        That(!fact.MatchesDeath(run,new(pending.Death,pending.Monster,pending.Result)),"复制prepared不能借原死亡引用");
        foreach(var mode in new[]{0,1,2,3})
        {
            var(other,a)=CreateRun(role,e=>mode switch{0=>e with{QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0,IsPractice=true},
                1=>e with{QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0,IsTutorial=true},2=>e with{QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0,IsQuestReplay=true},_=>e});
            var p=other.PrepareConfirmedDeath(a.Death(other.Snapshot.Identity,20)).Pending!;
            p.FreezeBattlePassActivity(other,clock);
            That(p.BattlePassActivity is null,"练习教程回放及任务剧情不冒普通死亡域");
        }
        var id=run.Snapshot.Identity;var old=CharacterFatigueState.NewOrdinary;
        var used=old.ConsumeAtEntry(5);
        foreach(var scope in new[]{"entry","room","hope_area"})
        {
            var f=BattlePassDungeonActivity.PrepareFatigue(id,scope,7,old,used,clock)!;
            That(f.Amount==5 && f.MatchesMission(0,new(4,10,100,3,"",-1)) &&
                !f.MatchesMission(0,new(1,10,100,1,"",-1)),"三真实消费域取原余额正差，仅接疲劳原定义");
        }
        var key = BattlePassDungeonActivity.PrepareFatigue(id,"room",7,old,used,clock)!.SourceId;
        That(key.Length == 32 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            "原疲劳来源满足现有表32hex合同");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"room",7,old,used,clock)!.SourceId == key,
            "同一原疲劳业务键重复规划摘要稳定");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"room",8,old,used,clock)!.SourceId != key,
            "不同原unit完整参与来源摘要");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"entry",7,old,used,clock)!.SourceId != key,
            "不同消费域完整参与来源摘要");
        That(BattlePassDungeonActivity.PrepareFatigue(id with { InstanceId = Guid.NewGuid() },"room",7,old,used,clock)!.SourceId != key,
            "不同实例完整参与来源摘要");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"room",7,old,old,clock) is null,"零消费不造疲劳来源");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"room",7,used,old,clock) is null,"恢复或退还不造消费");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"reset",7,old,used,clock) is null,"重置不冒消费域");
        That(BattlePassDungeonActivity.PrepareFatigue(id,"entry",7,old,used with{Maximum=157},clock) is null,
            "容量及其余疲劳owner字段变化不能伪装消费");
        That(BattlePassDungeonActivity.PrepareFatigue(id with{InstanceId=Guid.Empty},"entry",7,old,used,clock) is null,
            "空实例不产来源");
        That(BattlePassDungeonActivity.PrepareFatigue(id with{CharacterId=2},"entry",7,old,used,clock)!.SourceId!=
            BattlePassDungeonActivity.PrepareFatigue(id,"entry",7,old,used,clock)!.SourceId,
            "同组队instance不同角色消费有独立唯一身份");
        Console.WriteLine($"PASS BattlePass normal death and real fatigue activity core ({Count-before} checks)");
    }
}
