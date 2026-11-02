using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.BattlePass;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    public static void RunBattlePassGoldenPaidCardCore()
    {
        int before=Count;
        var role=new Character(1,0,"GoldenCore",0,Location,Location);
        var (run,authority)=CreateRun(role);
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity,20));
        var at=new DateTimeOffset(2026,11,4,7,0,0,TimeSpan.FromHours(8));
        var paid=new DungeonCardReward(100,null,"fixture-paid","fixture/card.etc",at.ToUnixTimeSeconds());
        var options=new DungeonCardOptions(paid,580,false,new());
        var prepared=run.PrepareSettlement(run.ClearFact!,[1,2,3],Reward,options:options).Pending!;
        var fact=BattlePassGoldenPaidCard.Prepare(prepared,role.Id,at)!;
        That(fact.Identity==run.Snapshot.Identity && fact.At==at && fact.SourceId.Length==32 &&
            fact.SourceId.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f'),"原黄金Paid侧绑定完整实例、原冻结领取时间及32hex");
        That(fact.SourceId==BattlePassGoldenPaidCard.Prepare(prepared,role.Id,at.AddMinutes(1))!.SourceId,
            "同Paid原事件重试来源稳定，不借周期时间生成新来源");
        var copy=new PreparedDungeonSettlement(prepared.Fact,[1,2,3],Reward,prepared.MapId,options:options);
        That(fact.SourceId!=BattlePassGoldenPaidCard.Prepare(copy,role.Id,at)!.SourceId,"不同原结算事件不能误合并");
        var waived=new PreparedDungeonSettlement(prepared.Fact,[1,2,3],Reward,prepared.MapId,
            options:options with { Price=0,UseGoldCard=true });
        That(BattlePassGoldenPaidCard.Prepare(waived,role.Id,at) is not null,"会员免金币仍是原Paid侧，不把会员资格作为必须条件");
        That(BattlePassGoldenPaidCard.Prepare(new(prepared.Fact,[1,2,3],Reward,prepared.MapId),role.Id,at) is null &&
            BattlePassGoldenPaidCard.Prepare(new(prepared.Fact,[1,2,3],Reward,prepared.MapId,options:options with { PaidDisabled=true }),role.Id,at) is null,
            "无Paid计划或原关闭Paid资格不建来源");
        Reject<InvalidDataException>(()=>BattlePassGoldenPaidCard.Prepare(prepared,2,at),"其它角色不能换绑原Paid实例");
        Reject<InvalidDataException>(()=>BattlePassGoldenPaidCard.Prepare(prepared,role.Id,DateTimeOffset.UnixEpoch.AddSeconds(-1)),"无效领取时间拒绝");
        foreach(var (category,mission) in new (byte,BattlePassMission)[]{(0,new(22,2,100,22,"fixture-golden",1)),(1,new(33,6,1000,22,"fixture-golden",1))})
        {
            That(BattlePassGoldenPaidCard.MatchesMission(category,mission),"只原日22和周33的raw22/param1完整tuple");
            foreach(byte wrongCategory in new byte[]{2,255})That(!BattlePassGoldenPaidCard.MatchesMission(wrongCategory,mission),"不扩原任务类别");
            That(!BattlePassGoldenPaidCard.MatchesMission((byte)(1-category),mission),"日周类别不可互换");
            foreach(var wrong in new[]{mission with { Id=34 },mission with { RawTypeCode=21 },mission with { RawParameter=-1 },
                mission with { RequiredCount=mission.RequiredCount+1 },mission with { Experience=mission.Experience+1 }})
                That(!BattlePassGoldenPaidCard.MatchesMission(category,wrong),"保原ID类型数量经验与参数");
        }
        Console.WriteLine($"PASS BattlePass raw22 original golden Paid card core ({Count-before} checks)");
    }
}
