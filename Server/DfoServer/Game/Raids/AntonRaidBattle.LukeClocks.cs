using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    LukeRaidClocks.Lease? lukeClockLease;
    bool lukeClockActive;

    // 仅准备已激活的正数显示值；不发送596，也不代表客户端RESET/早退清理已实现。
    internal uint? LukeRuleTimerSeconds(Guid instance, int phase, RaidTimer timer, long generation)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || instance != Instance || phase != current.Phase || generation <= 0 ||
                timer.Kind is not (1 or 2) || timer.DungeonId <= 0 ||
                !script.Phases.Any(row => row.Dungeons.Any(dungeon => dungeon.Id == timer.DungeonId)) ||
                !lukeClockActive || lukeClockLease is not { IsStopped: false } || members.Count == 0 ||
                current.ClockFaulted || current.PhaseExpired || pendingTransition is not null || TerminalRequested(current) ||
                LukePhaseDueLocked() || current.TimerGenerations.GetValueOrDefault(timer) != generation ||
                !current.Timers.TryGetValue(timer, out int duration) || duration is <= 0 or > 86400 ||
                !current.TimerAnchors.TryGetValue(timer, out long anchor)) return null;
            var remaining = TimeSpan.FromSeconds(duration) - lukeClock.GetElapsedTime(anchor);
            if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(duration)) return null;
            return checked((uint)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    internal void ActivateLukeClock(LukeRaidClocks.Lease lease)
    {
        lock (gate)
        {
            if ((Kind != RaidKind.Luke && !HasAntonRewards) || lease.Battle != this || lease.IsStopped || lukeClockLease is not null || members.Count == 0)
            { lease.Dispose(); return; }
            lukeClockLease = lease; lukeClockActive = true;
            // 构造Battle发生在费用SQL之前；初始SET从实际提交后的激活时刻起算。
            long now = lukeClock.GetTimestamp();
            current = current with { TimerAnchors = current.Timers.Keys.ToImmutableDictionary(timer => timer, _ => now),
                DeferredAnchors = current.Deferred.Keys.ToImmutableDictionary(token => token, _ => now) };
            AnchorLukePhaseLocked();
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
        if (lease.RetryRemaining is { } retryDelay && (PendingLukeSettlement() is not null || PendingAntonSettlement() is not null))
        { lease.Schedule(retryDelay); return; }
        // 正常请求可能先恢复原候选；旧退避不能继续压住下一phase的新SET。
        lease.SetRetry(null);
        if (pendingTransition is not null || TerminalRequested(current))
        { lease.Schedule(Timeout.InfiniteTimeSpan); return; }
        TimeSpan? earliest = LukePhaseRemainingLocked();
        foreach (var timer in current.Timers)
        {
            if (!current.TimerAnchors.TryGetValue(timer.Key, out long anchor)) continue;
            var remaining = TimeSpan.FromSeconds(timer.Value) - lukeClock.GetElapsedTime(anchor);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (earliest is null || remaining < earliest) earliest = remaining;
        }
        if (Kind == RaidKind.Anton)
            foreach (var action in current.Deferred.Where(row => row.Value.Tag == "[reserve dungeon state]"))
            {
                if (!current.DeferredAnchors.TryGetValue(action.Key, out long anchor)) continue;
                var remaining = TimeSpan.FromSeconds(action.Value.RawIntegers[^1]) - lukeClock.GetElapsedTime(anchor);
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
            if (LukePhaseDueLocked())
            {
                var expired = current with { PhaseExpired = true, Revision = current.Revision + 1,
                    Dungeons = current.Dungeons.ToImmutableDictionary(row => row.Key, _ => RaidDungeonState.Hide),
                    Timers = ImmutableDictionary<RaidTimer, int>.Empty,
                    TimerAnchors = ImmutableDictionary<RaidTimer, long>.Empty,
                    TimerGenerations = ImmutableDictionary<RaidTimer, long>.Empty,
                    Deferred = ImmutableDictionary<int, RaidClause>.Empty,
                    DeferredAnchors = ImmutableDictionary<int, long>.Empty, DeferredGenerations = ImmutableDictionary<int, long>.Empty };
                if (Kind == RaidKind.Anton) CommitBattleCandidate(before, expired);
                else current = expired;
                return true;
            }
            if (Kind == RaidKind.Anton)
            {
                ExpireDueAntonClockLocked();
                return !ReferenceEquals(before, current);
            }
            var due = current.Timers.Where(row => current.TimerAnchors.TryGetValue(row.Key, out long anchor) &&
                lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value))
                .Select(row => (Timer: row.Key, Generation: current.TimerGenerations.GetValueOrDefault(row.Key))).ToArray();
            foreach (var row in due) _ = ExpireLukeTimer(Instance, row.Timer, row.Generation);
            return !ReferenceEquals(before, current);
        }
    }
}
