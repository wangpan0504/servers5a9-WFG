namespace DfoServer.Testing.Inventory;

// 测试用一次性虚拟时钟，定时器注册/释放有可观测信号，不等待真实服务日。
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
    public async Task WaitForTimersAsync(int count)
    {
        while (true)
        {
            Task signal;
            lock (sync)
            {
                if (timers.Count(row => row.Due != long.MaxValue) == count) return;
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
            foreach (var timer in due) timer.Due = long.MaxValue;
            Signal();
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    sealed class ClockTimer(DailyBoosterTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; set; } = long.MaxValue;
        bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("测试只支持一次性定时器");
            lock (clock.sync)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(clock.ticks + dueTime.Ticks);
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
