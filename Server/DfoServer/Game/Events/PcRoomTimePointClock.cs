namespace DfoServer.Game.Events;

internal sealed record PcRoomTimePointClockReading(Guid SegmentId, DateTimeOffset At, bool Enabled,
    long ActivityRevision, IReadOnlyList<PcRoomTimePointElapsed> Totals);

// 每次真正入城选角使用一个段；只保存服务端单调时钟经过的时间，GM回调不取账号锁、不读库。
internal sealed class PcRoomTimePointClock : IDisposable
{
    readonly object gate = new();
    readonly ServerEventService events;
    readonly TimeProvider time;
    readonly Dictionary<int, long> dayTicks = [];
    readonly Guid segmentId = Guid.NewGuid();
    DateTimeOffset at;
    long timestamp;
    long revision = -1;
    bool enabled, stopped;

    public PcRoomTimePointClock(ServerEventService events, TimeProvider time)
    {
        this.events = events;
        this.time = time;
        // 与GM提交和Changed投递共用边界锁，顺序固定为活动锁→本段锁。
        events.SynchronizeClock(() =>
        {
            at = time.GetUtcNow();
            timestamp = time.GetTimestamp();
            events.Changed += Changed;
            try
            {
                var initial = events.Read(228);
                revision = initial.Schedule.Revision;
                enabled = initial.Active;
            }
            catch { events.Changed -= Changed; throw; }
            return true;
        });
    }

    void Changed(ServerEventChange change)
    {
        if (change.State.Schedule.EventId != 228) return;
        lock (gate)
        {
            if (stopped || change.State.Schedule.Revision <= revision) return;
            Advance(change.Timestamp);
            revision = change.State.Schedule.Revision;
            enabled = change.State.Active;
        }
    }

    public PcRoomTimePointClockReading Capture() => events.SynchronizeClock(() =>
    {
        lock (gate)
        {
            if (!stopped) Advance(time.GetTimestamp());
            return Reading();
        }
    });

    public void Dispose() => events.SynchronizeClock(() =>
    {
        lock (gate) stopped = true;
        events.Changed -= Changed;
        return true;
    });

    PcRoomTimePointClockReading Reading() => new(segmentId, at, enabled, revision,
        dayTicks.OrderBy(row => row.Key).Select(row => new PcRoomTimePointElapsed(row.Key,
            row.Value / TimeSpan.TicksPerMillisecond)).ToArray());

    void Advance(long nextTimestamp)
    {
        if (nextTimestamp <= timestamp) return;
        long ticks = time.GetElapsedTime(timestamp, nextTimestamp).Ticks;
        timestamp = nextTimestamp;
        if (ticks <= 0) return;
        var end = at.AddTicks(ticks);
        if (enabled)
        {
            while (at < end)
            {
                int day = PcRoomTimePointDay.At(at);
                var boundary = PcRoomTimePointDay.NextBoundary(at);
                var until = boundary < end ? boundary : end;
                dayTicks[day] = checked(dayTicks.GetValueOrDefault(day) + (until - at).Ticks);
                at = until;
            }
        }
        else at = end;
    }
}
