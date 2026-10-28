using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTotalBattleTests
{
    public static void RunCore()
    {
        int before = Count;
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var fighters = ImmutableArray.Create(
            new PvpArenaFighter(Guid.NewGuid(), 10, 0, 0) { Account = "total-one", TotalTeam = Team("总力甲", 10, 11, 12), Rating = Rating() },
            new PvpArenaFighter(Guid.NewGuid(), 20, 1, 1) { Account = "total-two", TotalTeam = Team("总力乙", 20, 21, 22), Rating = Rating() });
        var a = fighters[0].Membership;
        var b = fighters[1].Membership;
        var match = Match();
        That(!match.SelectTotal(match.Id, a, 0, now) && match.MarkLoaded(match.Id, a, now) &&
            match.Phase == PvpArenaMatchPhase.Loading && !match.SelectTotal(match.Id, a, 0, now), "总力加载完成前不接受选人");
        That(match.MarkLoaded(match.Id, b, now) && match.Phase == PvpArenaMatchPhase.Selecting &&
            !match.IsActive(a), "双方实际加载后进入选人，不能提前报告死亡");
        That(!match.SelectTotal(Guid.NewGuid(), a, 0, now) && !match.SelectTotal(match.Id, a, 3, now) &&
            !match.SelectTotal(match.Id, Guid.NewGuid(), 0, now), "选人绑定原局、原成员和三个有效索引");
        That(match.SelectTotal(match.Id, a, 2, now) && !match.SelectTotal(match.Id, a, 1, now) &&
            match.Phase == PvpArenaMatchPhase.Selecting && match.SelectTotal(match.Id, b, 0, now) &&
            match.Phase == PvpArenaMatchPhase.Fighting, "双方选人后才开战，已确认选择不能被重复请求替换");
        for (byte index = 0; index < 3; index++)
        {
            That(match.ReportDeath(match.Id, b, a, now) && match.Kills(a) == index + 1u, "只记录本回合真实击杀");
            if (index == 2) break;
            That(match.Result is null && match.Phase == PvpArenaMatchPhase.Selecting &&
                match.Total!.Choices[0].Index == 2 && !match.ReportDeath(match.Id, b, a, now) &&
                !match.SelectTotal(match.Id, a, 0, now) && !match.SelectTotal(match.Id, b, index, now),
                "单角色死亡只耗尽本人，胜者保留原角色，选人阶段拒绝迟到死亡及复用败者");
            That(match.SelectTotal(match.Id, b, (byte)(index + 1), now) && match.Phase == PvpArenaMatchPhase.Fighting,
                "败者换上未耗尽角色继续同一整局");
        }
        That(match.Result is { Forfeit: false } && match.Result.Standings.Single(row => row.Fighter.Membership == a).Outcome == PvpArenaOutcome.Win &&
            match.Result.Standings.Single(row => row.Fighter.Membership == b).Outcome == PvpArenaOutcome.Lose,
            "耗尽三名角色才生成一次整局胜负");
        var record = match.CaptureRecord(Guid.NewGuid(), 296, now, PvpRatingMode.Total)!;
        That(record.Participants.All(row => row.TotalTeam is { Complete: true } && row.Rating is not null),
            "总力整局冻结六角色队伍与双方独立赛前分");

        var timeout = Match(); Load(timeout);
        That(!timeout.ExpireClock(now.AddSeconds(44)) && timeout.ExpireClock(now.AddSeconds(45)) &&
            timeout.Phase == PvpArenaMatchPhase.Fighting && timeout.Total!.Choices.All(row => row.Index == 0),
            "45秒兼容选人期限前不猜选择，届满自动选择各自首个未耗尽角色");
        That(!timeout.ResolveTimeout(timeout.Id, a, now.AddSeconds(104)) &&
            timeout.ResolveTimeout(timeout.Id, a, now.AddSeconds(105)) && timeout.Result is null &&
            timeout.Phase == PvpArenaMatchPhase.Selecting, "回合时钟重新计算，超时仅耗尽负方当前角色");
        That(timeout.SelectTotal(timeout.Id, b, 1, now.AddSeconds(106)) && timeout.Total!.Choices[0].Index == 0 &&
            !timeout.ResolveTimeout(timeout.Id, a, now.AddSeconds(165)), "新回合保留胜者并重置期限，旧回合时钟不能结束新回合");
        var draw = Match(); Load(draw);
        for (int round = 0; round < 3; round++)
        {
            var start = now.AddSeconds(round * 61);
            That(draw.SelectTotal(draw.Id, a, (byte)round, start) && draw.SelectTotal(draw.Id, b, (byte)round, start), "平局后双方使用下一名角色");
            That(draw.ResolveTimeout(draw.Id, null, start.AddSeconds(60)), "平局耗尽双方当回合角色");
        }
        That(draw.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Draw), "三回合均平局后整局只保存平局");
        var departing = Match(); Load(departing);
        That(departing.Depart(departing.Id, b) && departing.Result is { Forfeit: true } &&
            departing.Result.Standings.Single(row => row.Fighter.Membership == a).Outcome == PvpArenaOutcome.Win,
            "选人阶段退出按整队弃权处理，不等待三次假死亡");
        var cancelled = Match();
        That(cancelled.ExpireLoading(cancelled.Id, now.AddSeconds(30)) &&
            cancelled.Result!.Standings.All(row => row.Outcome == PvpArenaOutcome.Cancelled), "总力加载失败无奖取消");
        Console.WriteLine($"PASS {Count - before} 总力加载、选人、多轮耗尽、超时与弃权断言");

        PvpArenaMatch Match() => new(PvpArenaMode.Individual, fighters, now, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
        void Load(PvpArenaMatch current) { current.MarkLoaded(current.Id, a, now); current.MarkLoaded(current.Id, b, now); }
        static PvpTotalTeam Team(string name, ushort first, ushort second, ushort third) => new(Guid.NewGuid(), 1, name, [first, second, third]);
        static PvpRatingSnapshot Rating() => new(0, new PvpRatingCalculator(PvpRatingTests.Fixture()).Initial());
    }
}
