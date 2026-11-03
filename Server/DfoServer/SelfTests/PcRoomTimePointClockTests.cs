using DfoServer.Game.Events;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomTimePointClockTests
{
    public static void RunCore()
    {
        int before = Count;
        var origin = DateTimeOffset.Parse("2026-10-28T01:00:00Z");
        var time = new ShiftClock(origin);
        var events = new ServerEventService(new EventStore(true), time);
        using (var clock = new PcRoomTimePointClock(events, time))
        {
            time.Advance(TimeSpan.FromTicks(4_000));
            That(Total(clock.Capture()) == 0, "不到一毫秒不提前累计");
            time.Advance(TimeSpan.FromTicks(6_000));
            That(Total(clock.Capture()) == 1, "重复查询保留本段分数毫秒，不丢零头");
            var first = clock.Capture();
            That(first.SegmentId == clock.Capture().SegmentId && Total(first) == Total(clock.Capture()),
                "重复检查点使用同一段绝对水位，不产生额外时长");
            time.Advance(TimeSpan.FromMilliseconds(249));
            events.Override(228, false);
            time.Advance(TimeSpan.FromSeconds(20));
            That(Total(clock.Capture()) == 250 && !clock.Capture().Enabled, "关闭区间不计在线，已开启部分保持");
            events.Override(228, true);
            time.Advance(TimeSpan.FromMilliseconds(100));
            events.Override(228, false);
            time.Advance(TimeSpan.FromMilliseconds(100));
            events.Override(228, true);
            time.Advance(TimeSpan.FromMilliseconds(100));
            That(Total(clock.Capture()) == 450 && clock.Capture().ActivityRevision == 4,
                "两次查询之间快速开关也准确分割开启和关闭区间");
        }
        time = new ShiftClock(DateTimeOffset.Parse("2026-10-28T21:59:59.500Z"));
        events = new ServerEventService(new EventStore(true), time);
        using (var clock = new PcRoomTimePointClock(events, time))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var reading = clock.Capture();
            That(reading.Totals.Count == 2 && reading.Totals[0] == new PcRoomTimePointElapsed(20261028, 500) &&
                reading.Totals[1] == new PcRoomTimePointElapsed(20261029, 500), "CST06跨日只拆真实在线区间，每日各保500毫秒");
        }
        time = new ShiftClock(origin);
        events = new ServerEventService(new EventStore(true), time);
        using (var clock = new PcRoomTimePointClock(events, time))
        {
            time.Advance(TimeSpan.FromSeconds(5));
            time.UtcShift = TimeSpan.FromDays(30);
            That(Total(clock.Capture()) == 5_000 && clock.Capture().At == origin.AddSeconds(5),
                "系统UTC向前跳变不能伪造在线时间或服务日");
            time.UtcShift = TimeSpan.FromDays(-30);
            time.Advance(TimeSpan.FromSeconds(1));
            That(Total(clock.Capture()) == 6_000 && clock.Capture().At == origin.AddSeconds(6),
                "系统UTC回拨后仍由同一单调时钟继续累计");
            var last = clock.Capture();
            clock.Dispose();
            time.Advance(TimeSpan.FromDays(1));
            events.Override(228, false);
            That(clock.Capture().At == last.At && Total(clock.Capture()) == Total(last),
                "段结束后退订活动变化，离线时间和后来的开关不再进入旧段");
        }
        ConcurrentBoundary(origin);
        Console.WriteLine($"PASS pcroom timepoint clock assertions {Count - before}");
    }

    static void ConcurrentBoundary(DateTimeOffset origin)
    {
        var time = new ShiftClock(origin);
        var events = new ServerEventService(new EventStore(true), time);
        using var clock = new PcRoomTimePointClock(events, time);
        using var timestampTaken = new ManualResetEventSlim();
        using var releaseChange = new ManualResetEventSlim();
        using var captureDone = new ManualResetEventSlim();
        int pause = 1;
        time.Advance(TimeSpan.FromSeconds(1));
        time.TimestampCaptured = () =>
        {
            if (Interlocked.Exchange(ref pause, 0) == 0) return;
            timestampTaken.Set();
            if (!releaseChange.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试GM边界未释放");
        };
        Exception? changeError = null, captureError = null;
        PcRoomTimePointClockReading? reading = null;
        var change = new Thread(() =>
        {
            try { events.Override(228, false); }
            catch (Exception error) { changeError = error; }
        }) { IsBackground = true };
        var capture = new Thread(() =>
        {
            try { reading = clock.Capture(); }
            catch (Exception error) { captureError = error; }
            finally { captureDone.Set(); }
        }) { IsBackground = true };
        bool captureStarted = false;
        try
        {
            change.Start();
            That(timestampTaken.Wait(TimeSpan.FromSeconds(5)), "GM已取确定时间戳但尚未投递变化");
            time.Advance(TimeSpan.FromSeconds(1));
            capture.Start(); captureStarted = true;
            That(SpinWait.SpinUntil(() => captureDone.IsSet ||
                (capture.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)) && !captureDone.IsSet,
                "查询采样等待已开始的GM边界完成，不能先积分到较晚的时间戳");
        }
        finally
        {
            releaseChange.Set();
            if (!change.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试GM线程未结束");
            if (captureStarted && !capture.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("测试采样线程未结束");
        }
        That(changeError is null && captureError is null && reading is { Enabled: false } && Total(reading) == 1000,
            "GM在一秒边界关闭，稍后查询不多计其后的一秒，检查点可直接提交绝对水位");
    }

    static long Total(PcRoomTimePointClockReading reading) => reading.Totals.Sum(row => row.Milliseconds);

    sealed class ShiftClock(DateTimeOffset origin) : TimeProvider
    {
        readonly DailyBoosterTestClock basis = new(origin);
        public TimeSpan UtcShift;
        public Action? TimestampCaptured;
        public override long TimestampFrequency => basis.TimestampFrequency;
        public override long GetTimestamp()
        {
            long value = basis.GetTimestamp();
            TimestampCaptured?.Invoke();
            return value;
        }
        public override DateTimeOffset GetUtcNow() => basis.GetUtcNow() + UtcShift;
        public void Advance(TimeSpan elapsed) => basis.Advance(elapsed);
    }

    sealed class EventStore(bool active) : IServerEventStore
    {
        ServerEventSchedule value = new(228, OverrideActive: active);
        public ServerEventSchedule Read(ushort eventId) => value;
        public ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired)
        {
            if (value != expected) throw new InvalidOperationException("测试活动版本冲突");
            return value = desired with { Revision = expected.Revision + 1 };
        }
    }
}
