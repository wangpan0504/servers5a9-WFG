using DfoServer.Game.Dungeons;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastRunProgressTests
{
    public static void RunCore()
    {
        int before = Count;
        int[][] scores = [[1300,1170,1040,840,720,600,440], [2600,2470,2340,2040,1920,1800,1540],
            [4200,3920,3640,3120,2860,2600,2160]];
        int[] ends = [60,120,240,460,580,700,int.MaxValue];
        foreach (int stage in Enumerable.Range(0, 6))
        for (int tier = 0; tier < 7; tier++)
        {
            That(BeastRunProgress.StageScore(stage, ends[tier]) == scores[Math.Min(stage, 2)][tier],
                "魔兽计分采用阶段分值及包含等号的时长档位");
            if (tier < 6) That(BeastRunProgress.StageScore(stage, ends[tier] + 1) == scores[Math.Min(stage, 2)][tier + 1],
                "魔兽计分超过边界才进入下一档");
        }
        foreach (var (score, rank) in new[] { (0,6),(2999,6),(3000,5),(4999,5),(5000,4),(7999,4),
            (8000,3),(9999,3),(10000,2),(13999,2),(14000,1),(15999,1),(16000,0),(20700,0) })
            That(BeastRunProgress.Rank(score) == rank, "魔兽排名阈值包含下限");

        var clock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        var run = new BeastRunProgress(Guid.NewGuid(), clock);
        That(run.TrySchedule(1) is null && !run.TryExpire() && run.View().RemainingMilliseconds == -1,
            "前两阶段没有调度或倒计时");
        That(!run.Enter(1) && run.Enter(0), "阶段必须依次进入");
        clock.Advance(TimeSpan.FromSeconds(60.999));
        var first = run.Prepare(0)!;
        That(first.Seconds == 60 && first.Score == 1300, "阶段耗时取整秒且至少一秒");
        clock.Advance(TimeSpan.FromSeconds(800));
        That(ReferenceEquals(run.Prepare(0), first) && run.Confirm(first) && run.Confirm(first) && run.View().Score == 1300,
            "事务重试保留原计分候选，重复确认不累计");
        That(!run.Enter(2) && run.Enter(1), "未完成第二阶段不能跳到第三阶段");
        clock.Advance(TimeSpan.FromSeconds(900));
        That(!run.TryExpire() && run.View().RemainingMilliseconds == -1, "第二阶段再久也不超时");
        That(run.Confirm(run.Prepare(1)!) && run.Enter(2) && run.View().RemainingMilliseconds == 180000,
            "第三阶段首次进入才启动共同180秒");
        using var lease = run.TrySchedule(1)!;
        That(run.TrySchedule(2) is null, "共享实例仅一个到期调度资格");
        clock.Advance(TimeSpan.FromSeconds(90));
        run.RemoveParticipant(1, last: false);
        using var transfer = run.TrySchedule(2)!;
        That(lease.Stop.IsCancellationRequested && !transfer.Stop.IsCancellationRequested && run.View().RemainingMilliseconds == 90000,
            "调度者退出取消原计时任务，转移保留同一deadline");
        That(run.Enter(2) && run.View().RemainingMilliseconds == 90000, "重访或队员投影不重置时钟");
        clock.Advance(TimeSpan.FromMilliseconds(89999));
        var edge = run.Prepare(2)!;
        clock.Advance(TimeSpan.FromMilliseconds(1));
        That(!run.TryExpire() && ReferenceEquals(run.Prepare(2), edge), "压线死亡冻结后优先完成原事务");
        That(run.Confirm(edge) && run.TryExpire() && !run.TryExpire() && run.View().CompletedStages == 3 &&
            run.View().Terminal == BeastTerminal.TimedOut && transfer.Stop.IsCancellationRequested,
            "原击杀提交后只生成一次超时终局，已得阶段及分数保留");
        That(!run.Enter(3) && run.Prepare(2) is null && !run.CanObserveDeath, "终局后迟到死亡和过图不可推进");

        var completeClock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        var complete = new BeastRunProgress(Guid.NewGuid(), completeClock);
        for (int stage = 0; stage < 6; stage++)
        {
            That(complete.Enter(stage), "正常通关依次绑定阶段");
            completeClock.Advance(TimeSpan.FromSeconds(1));
            That(complete.Confirm(complete.Prepare(stage)!), "正常通关确认六次指定Boss计分");
        }
        completeClock.Advance(TimeSpan.FromSeconds(180));
        That(complete.View().Terminal == BeastTerminal.Cleared && complete.View().Score == 20700 && !complete.TryExpire(),
            "第六指定Boss先提交时超时不能覆盖通关终局");

        var exactClock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        var exact = new BeastRunProgress(Guid.NewGuid(), exactClock);
        for (int stage = 0; stage < 2; stage++) { exact.Enter(stage); exact.Confirm(exact.Prepare(stage)!); }
        exact.Enter(2);
        exactClock.Advance(TimeSpan.FromSeconds(180));
        That(exact.Prepare(2) is null && !exact.CanObserveDeath && exact.TryExpire() && exact.View().CompletedStages == 2,
            "恰好180秒未冻结的死亡不得获得阶段罐或分数");
        var abandoned = new BeastRunProgress(Guid.NewGuid(), exactClock);
        abandoned.RemoveParticipant(1, last: true);
        That(abandoned.View().Terminal == BeastTerminal.Abandoned && !abandoned.Enter(0), "最后成员退出关闭本实例");
        Console.WriteLine($"PASS {Count - before} beast score and terminal assertions");
    }
}
