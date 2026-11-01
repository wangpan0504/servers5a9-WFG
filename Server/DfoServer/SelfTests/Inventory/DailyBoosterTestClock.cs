namespace DfoServer.Testing.Inventory;

// 测试虚拟时钟；周期回调按推进后的到期状态合并，定时器注册和释放仍可观测；时区可注入以覆盖服务日边界。
internal sealed class DailyBoosterTestClock(DateTimeOffset origin, TimeZoneInfo? zone = null) : TimeProvider
{
    readonly object sync = new();
    readonly List<ClockTimer> timers = [];
    TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long ticks;
    public override TimeZoneInfo LocalTimeZone => zone ?? base.LocalTimeZone;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (sync) return ticks; }
    public override DateTimeOffset GetUtcNow() { lock (sync) return origin.AddTicks(ticks); }
    public int ActiveTimers { get { lock (sync) return timers.Count(row => row.Due != long.MaxValue); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        lock (sync) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    void Signal()
    {
        var previous = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
    // 连接日界使用一次性等待；共享周期轮询不属于连接的注册/释放数量。
    public async Task WaitForOneShotTimersAsync(int count)
    {
        while (true)
        {
            Task signal;
            lock (sync)
            {
                if (timers.Count(row => row.Period == 0 && row.Due != long.MaxValue) == count) return;
                signal = changed.Task;
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    // 推进虚拟时间前确认指定活动边界已注册，避免把尚未创建的延时永远留在未来。
    public async Task WaitForOneShotTimerAsync(DateTimeOffset dueAt)
    {
        long due = (dueAt - origin).Ticks;
        while (true)
        {
            Task signal;
            lock (sync)
            {
                if (timers.Any(row => row.Period == 0 && row.Due == due)) return;
                signal = changed.Task;
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    public void Advance(TimeSpan amount)
    {
        ClockTimer[] due;
        lock (sync)
        {
            ticks = checked(ticks + amount.Ticks);
            due = timers.Where(row => row.Due <= ticks).ToArray();
            foreach (var timer in due)
                timer.Due = timer.Period > 0
                    ? checked(timer.Due + ((ticks - timer.Due) / timer.Period + 1) * timer.Period)
                    : long.MaxValue;
            Signal();
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    sealed class ClockTimer(DailyBoosterTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; } = long.MaxValue;
        public long Period { get; private set; }
        bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(period));
            lock (clock.sync)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.ticks + dueTime.Ticks);
                Period = period > TimeSpan.Zero ? period.Ticks : 0;
                clock.Signal();
                return true;
            }
        }
        public void Dispose()
        {
            lock (clock.sync)
            {
                disposed = true;
                clock.timers.Remove(this);
                clock.Signal();
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
