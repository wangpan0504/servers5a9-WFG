using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassBossRushRewardCore()
    {
        int before=Count;
        var at=new DateTimeOffset(2026,11,1,7,0,0,TimeSpan.FromHours(8));
        var clock=new BossRushRewardClock(at);
        var definition=new DungeonDefinition(337,"dungeon/bossrush/core.dgn",null,null,[],[]) { DungeonType="boss rush dungeon" };
        var role=new Character(1,0,"RushCore",0,Location,Location);
        var (run,authority)=CreateRun(role,entry=>entry with
            { DungeonId=337,QuestId=0,ActiveQuestSlot=byte.MaxValue,RouteQuestId=0,IsBossRush=true,TimeoutMilliseconds=120000 });
        var catalog=BossRushRewardFixtureCatalog();var rush=new BossRushRunState(catalog,120000);
        That(rush.TryScore(100) && rush.TryScore(101) && rush.Rank>0,"原计分目录计算具名段位，不猜任务分数阈值");
        var core=new ItemCore(1001,ItemKind.Material,10);var inventory=role.Inventory;
        var granted=inventory with { Items=[new(0,new(InventoryListType.Main,9),core)] };
        var reward=new BossRushClearReward(rush.Rank,rush.Score,new(1001,10000,10),
            new(new(inventory,granted,"boss_rush_reward"),[new(0,core,BossRushClearReward.Rule,BossRushCatalog.ResourcePath,at.ToUnixTimeSeconds())],[],0));
        That(BattlePassBossRushReward.Prepare(definition,run.Entry,new(run.Snapshot.Identity,337,[]),rush,reward,clock) is null,
            "计分有段位但无真正原终末清图不建领奖来源");
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
        var pending=run.PrepareSettlement(run.ClearFact!,[1,2,3],Reward,bossRush:reward).Pending!;
        pending.FreezeBattlePassBossRushReward(definition,run.Entry,rush,clock);
        var source=pending.BattlePassBossRushReward!;
        That(source.Identity==run.Snapshot.Identity && ReferenceEquals(source.Reward,reward) && source.PlannedAt==at &&
            source.SourceId.Length==32 && source.Matches(run,pending),"原终末与原段位交付对象共同冻结32hex领奖来源");
        clock.Now=at.AddMinutes(1);pending.FreezeBattlePassBossRushReward(definition,run.Entry,rush,clock);
        That(ReferenceEquals(source,pending.BattlePassBossRushReward) && source.PlannedAt==at,"同冻结计划重试不重采奖励时间");
        var repeated=BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact!,rush,reward,clock)!;
        That(source.SourceId==repeated.SourceId,"同完整实例身份确定性来源");
        var another=BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact! with
            { Identity=run.ClearFact!.Identity with { InstanceId=Guid.NewGuid() } },rush,reward,clock)!;
        That(source.SourceId!=another.SourceId,"不同原正式实例不误去重");
        var copied=new PreparedDungeonSettlement(run.ClearFact!,[1,2,3],Reward,pending.MapId,bossRush:reward with { });
        That(!source.Matches(run,copied),"复制奖励对象不能改绑原冻结交付");
        var mission=new BattlePassMission(8,1,100,7,"fixture-rush-reward",-1);
        That(BattlePassBossRushReward.MatchesMission(0,mission),"原日8/raw7/1/100/-1精确匹配");
        foreach(byte category in new byte[]{1,2,255})That(!BattlePassBossRushReward.MatchesMission(category,mission),"不扩原类别");
        foreach(var wrong in new[]{mission with { Id=16 },mission with { RawTypeCode=45 },mission with { RawParameter=1 },
            mission with { RequiredCount=2 },mission with { Experience=101 }})
            That(!BattlePassBossRushReward.MatchesMission(0,wrong),"不改原定义数量经验参数");
        foreach(var excluded in new[]{run.Entry with { IsPractice=true },run.Entry with { IsTutorial=true },
            run.Entry with { IsQuestReplay=true },run.Entry with { IsBossRush=false },run.Entry with { RouteQuestId=3145 },
            run.Entry with { RaidEntry=new RaidDungeonIdentity(Guid.NewGuid(),1,0) }})
            That(BattlePassBossRushReward.Prepare(definition,excluded,run.ClearFact!,rush,reward,clock) is null,
                "原练习教程重放攻坚剧情及非灵魂之冢入口不建领奖来源");
        foreach(var excluded in new[]{reward with { Rank=0,Item=null },reward with { Score=0 },reward with { Item=new(1002,10000,10) },
            reward with { Delivery=reward.Delivery with { Inventory=new(inventory,inventory,"no_reward"),MailItems=[] } }})
            That(BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact!,rush,excluded,clock) is null,
                "无奖段位分数或非原专表物品及无实际交付计划不计");
        That(BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact!,null,reward,clock) is null &&
            BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact!,rush,null,clock) is null &&
            BattlePassBossRushReward.Prepare(definition with { DungeonType=null },run.Entry,run.ClearFact!,rush,reward,clock) is null,
            "缺原计分状态或原段位奖励资源类型不计");
        That(BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact! with { ClearMap=null },rush,reward,clock) is null &&
            BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact! with { ConfirmedDeathEvents=[] },rush,reward,clock) is null,
            "非终末或无原权威确认死亡不计");
        var mailed=reward with { Delivery=reward.Delivery with { Inventory=new(inventory,inventory,"mail_reward"),MailItems=[core] } };
        That(BattlePassBossRushReward.Prepare(definition,run.Entry,run.ClearFact!,rush,mailed,clock) is not null,
            "保留原满包永久溢出邮件交付资格，不强制奖励入包");
        Console.WriteLine($"PASS BattlePass BossRush actual reward core ({Count-before} checks)");
    }
    static BossRushCatalog BossRushRewardFixtureCatalog()
    {
        var tokens=new List<PvfToken>();var text=new Dictionary<int,string>();int key=0;
        void Tag(string value){text[++key]=value;tokens.Add(new(3,key));}
        void Scalar(string name,int value){Tag("["+name+"]");tokens.Add(new(0,value));}
        void Block(string name,IEnumerable<int> values){Tag("["+name+"]");tokens.AddRange(values.Select(value=>new PvfToken(0,value)));Tag("[/"+name+"]");}
        Block("boss list",Enumerable.Range(100,24));Scalar("boss type count",4);Scalar("named type count",8);
        Block("soul point",[5,10,15]);Block("rank info",[20,40,65,100]);
        Block("rank reward",Enumerable.Range(1,4).SelectMany(rank=>new[]{rank,1001,10,10000}));
        return new(tokens,id=>text[id]);
    }
    sealed class BossRushRewardClock(DateTimeOffset now):TimeProvider
    {
        public DateTimeOffset Now {get;set;}=now;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
