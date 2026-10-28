using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 本服计时合同沿LukeRaidClocks/LukeRules的FromSeconds，不宣称已证明客户端转换单位。
    // 只调度明确的SET TIMER和RESERVE DUNGEON STATE；随机孵化目标仍须独立证据。
    void ExpireDueAntonClockLocked()
    {
        var timers = current.Timers.Where(row => current.TimerAnchors.TryGetValue(row.Key, out long anchor) &&
            lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value))
            .Select(row => (row.Key, Generation: current.TimerGenerations.GetValueOrDefault(row.Key))).ToArray();
        var deferred = current.Deferred.Where(row => row.Value.Tag == "[reserve dungeon state]" &&
            current.DeferredAnchors.TryGetValue(row.Key, out long anchor) &&
            lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value.RawIntegers[^1]))
            .Select(row => (row.Key, Generation: current.DeferredGenerations[row.Key])).ToArray();
        foreach (var row in timers)
        {
            if (TerminalRequested(current) || pendingTransition is not null) return;
            if (current.TimerGenerations.GetValueOrDefault(row.Key) != row.Generation) continue;
            var before = current;
            CommitBattleCandidate(before, Evaluate(before with { Timers = before.Timers.Remove(row.Key),
                TimerAnchors = before.TimerAnchors.Remove(row.Key), TimerGenerations = before.TimerGenerations.Remove(row.Key) },
                null, null, row.Key));
        }
        foreach (var row in deferred)
        {
            if (TerminalRequested(current) || pendingTransition is not null) return;
            if (current.DeferredGenerations.GetValueOrDefault(row.Key) != row.Generation) continue;
            _ = CompleteDeferred(Instance, current.Revision, row.Key);
        }
    }
}
