using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    LukeRaidClocks.Lease? lukeClockLease;
    bool lukeClockActive;

    internal void ActivateLukeClock(LukeRaidClocks.Lease lease)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || lease.Battle != this || lease.IsStopped || lukeClockLease is not null || members.Count == 0)
            { lease.Dispose(); return; }
            lukeClockLease = lease; lukeClockActive = true;
            // 构造Battle发生在费用SQL之前；初始SET从实际提交后的激活时刻起算。
            long now = lukeClock.GetTimestamp();
            current = current with { TimerAnchors = current.Timers.Keys.ToImmutableDictionary(timer => timer, _ => now) };
            ScheduleLukeClockLocked();
        }
    }

    internal void FaultLukeClock(LukeRaidClocks.Lease lease)
    {
        lock (gate)
            if (ReferenceEquals(lukeClockLease, lease))
                current = current with { ClockFaulted = true, Revision = current.Revision + 1 };
    }

    internal void StopLukeClock(LukeRaidClocks.Lease lease)
    {
        lock (gate)
            if (ReferenceEquals(lukeClockLease, lease)) { lukeClockLease = null; lukeClockActive = false; }
    }

    internal void ScheduleLukeClock(LukeRaidClocks.Lease lease, TimeSpan? retry = null)
    {
        lock (gate)
            if (ReferenceEquals(lukeClockLease, lease))
            { lease.SetRetry(retry); ScheduleLukeClockLocked(); }
    }

    void ScheduleLukeClockLocked()
    {
        if (!lukeClockActive || lukeClockLease is not { IsStopped: false } lease) return;
        if (members.Count == 0 || current.State == AntonRaidBattleState.Completed)
        { lease.Dispose(); return; }
        if (lease.RetryRemaining is { } retryDelay && PendingLukeSettlement() is not null)
        { lease.Schedule(retryDelay); return; }
        // 正常请求可能先恢复原候选；旧退避不能继续压住下一phase的新SET。
        lease.SetRetry(null);
        if (pendingTransition is not null || TerminalRequested(current) || current.Timers.IsEmpty)
        { lease.Schedule(Timeout.InfiniteTimeSpan); return; }
        TimeSpan? earliest = null;
        foreach (var timer in current.Timers)
        {
            if (!current.TimerAnchors.TryGetValue(timer.Key, out long anchor)) continue;
            var remaining = TimeSpan.FromSeconds(timer.Value) - lukeClock.GetElapsedTime(anchor);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (earliest is null || remaining < earliest) earliest = remaining;
        }
        lease.Schedule(earliest ?? Timeout.InfiniteTimeSpan);
    }

    internal bool ExpireDueLukeClock(LukeRaidClocks.Lease lease)
    {
        lock (gate)
        {
            if (!ReferenceEquals(lukeClockLease, lease) || lease.IsStopped || !lukeClockActive || members.Count == 0 ||
                pendingTransition is not null || TerminalRequested(current)) return false;
            // 本实例最多30个(kind,DGN)键；只处理当前已到期代，不在一次回调内追逐新建零秒timer。
            var before = current;
            var due = current.Timers.Where(row => current.TimerAnchors.TryGetValue(row.Key, out long anchor) &&
                lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value))
                .Select(row => (Timer: row.Key, Generation: current.TimerGenerations.GetValueOrDefault(row.Key))).ToArray();
            foreach (var row in due) _ = ExpireLukeTimer(Instance, row.Timer, row.Generation);
            return !ReferenceEquals(before, current);
        }
    }
}
