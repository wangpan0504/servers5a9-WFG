using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using DfoServer.Network.Projection;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static async Task TagEvildomRunSchedulingAsync()
    {
        await TagEvildomInstalledRunAsync();
        TagEvildomClearTransaction();
        TagEvildomChallengeTransaction();
        using (var fixture = new Fixture())
        {
            var session = fixture.Session;
            var timeout = new TagEvildomScheduledRevival(99, fixture.Owner.Generation, session.SelectionEpoch,
                new(Guid.NewGuid(), 0, 1, 12_000));
            That(session.SwitchFormalTagEvildom(1) is null && session.ReportFormalTagEvildomDeath(0) is null &&
                session.CurrentTagEvildomRevival() is null && session.ReviveFormalTagEvildom(timeout, 12_000) is null,
                "未选角不能借TAG用例创建正式实例或定时任务");
            fixture.Select();
            var selected = session.Selected;
            var data = fixture.Snapshot();
            That(session.SwitchFormalTagEvildom(1) is null && session.ReportFormalTagEvildomDeath(0) is null &&
                session.CurrentTagEvildomRevival() is null && session.ReviveFormalTagEvildom(timeout, 12_000) is null &&
                ReferenceEquals(selected, session.Selected) && fixture.Snapshot() == data,
                "普通城镇和无正式TAG Run没有成功回执，不换Selected、不写资产");
            session.ReturnToRoster();
            fixture.Select();
            That(session.ReviveFormalTagEvildom(timeout, 20_000) is null, "旧选角epoch的任务不能进入重登角色");
            using var takeover = fixture.Owners.Acquire("planning", () => { });
            Reject<InvalidOperationException>(() => session.CurrentTagEvildomRevival(), "旧账号owner不能读取/推进新的TAG生命周期");
        }

        // 直接调用生产调度所用的等待路径；不靠真12秒sleep、不向Session注入伪正式准入。
        var clock = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        using var stop = new CancellationTokenSource();
        var wait = TagEvildomRevivalProjection.WaitUntilAsync(12_000, clock, stop.Token);
        await clock.WaitForTimersAsync(1);
        clock.Advance(TimeSpan.FromMilliseconds(11_999));
        That(!wait.IsCompleted && clock.ActiveTimers == 1, "真实等待路径到12秒前仍保留唯一一次性定时器");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForTimersAsync(0);
        That(wait.IsCompletedSuccessfully, "可控时钟到期使生产等待路径结束，不等待真实墙钟");
        using var cancelled = new CancellationTokenSource();
        var cancelledWait = TagEvildomRevivalProjection.WaitUntilAsync(24_000, clock, cancelled.Token);
        await clock.WaitForTimersAsync(1);
        await cancelled.CancelAsync();
        try { await cancelledWait; throw new InvalidOperationException("取消后不能成功完成到期等待"); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        await clock.WaitForTimersAsync(0);
        That(cancelledWait.IsCanceled, "退场/断线取消令牌释放实际定时器，不留下后台长任务");
        await TagEvildomRevivalProjection.WaitUntilAsync(11_000, clock, CancellationToken.None);
        That(clock.ActiveTimers == 0, "已经到期无需注册第二个定时器");

        var monotonic = new DailyBoosterTestClock(DateTimeOffset.UnixEpoch);
        var wallClock = new TagEvildomWallClock(monotonic);
        var rollbackWait = TagEvildomRevivalProjection.WaitUntilAsync(12_000, wallClock, CancellationToken.None);
        await monotonic.WaitForTimersAsync(1);
        wallClock.Offset = TimeSpan.FromSeconds(-6);
        monotonic.Advance(TimeSpan.FromSeconds(12));
        await monotonic.WaitForTimersAsync(1);
        That(!rollbackWait.IsCompleted, "墙钟回拨后首个物理计时器到期不等于角色复活截止");
        monotonic.Advance(TimeSpan.FromSeconds(6));
        await rollbackWait.WaitAsync(TimeSpan.FromSeconds(5));
        await monotonic.WaitForTimersAsync(0);
        That(rollbackWait.IsCompletedSuccessfully, "原截止仍按当前墙钟重新等待，未提前发布");

        using var gate = new SemaphoreSlim(0, 1);
        wallClock.Offset = TimeSpan.Zero;
        var gated = TagEvildomRevivalProjection.WaitForCommandTurnAsync(18_000, wallClock, gate, CancellationToken.None);
        That(!gated.IsCompleted, "已经到期的任务仍等待现有连接命令门禁");
        wallClock.Offset = TimeSpan.FromSeconds(-6);
        gate.Release();
        await monotonic.WaitForTimersAsync(1);
        That(!gated.IsCompleted && gate.CurrentCount == 1, "获得门禁前墙钟回拨时释放门禁并重挂唯一截止，而非静默丢任务");
        monotonic.Advance(TimeSpan.FromSeconds(6));
        await gated.WaitAsync(TimeSpan.FromSeconds(5));
        That(gate.CurrentCount == 0, "实际截止再次达到后持有门禁交给提交方");
        gate.Release();
    }

    // 复用现有可观测定时器，只把墙钟偏移与单调计时分离，覆盖生产等待中的回拨分支。
    sealed class TagEvildomWallClock(DailyBoosterTestClock inner) : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            inner.CreateTimer(callback, state, dueTime, period);
    }
}
