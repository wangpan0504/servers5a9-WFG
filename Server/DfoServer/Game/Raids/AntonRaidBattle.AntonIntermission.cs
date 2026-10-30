namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    int AntonHeaderSeconds(string tag) => script.Headers.Single(row => row.Tag == tag).Values.Single().Number;

    TimeSpan? AntonWaitRemainingLocked() => HasAntonRewards && !current.ClockFaulted &&
        current.State == AntonRaidBattleState.Intermission && current.AntonWaitSeconds > 0
        ? TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(current.AntonWaitSeconds) -
            lukeClock.GetElapsedTime(current.AntonWaitAnchor)).Ticks)) : null;

    internal uint? AntonWaitSeconds(AntonRaidBattleView expected)
    {
        lock (gate)
        {
            if (!ReferenceEquals(current, expected) || current.ClockFaulted || current.State != AntonRaidBattleState.Intermission) return null;
            return AntonWaitRemainingLocked() is { } remaining ? checked((uint)Math.Ceiling(remaining.TotalSeconds)) : 0;
        }
    }

    // Handler已核对团长；本层仍校验本次actor与当前实例，不把重复666续成新deadline。
    internal bool PrepareAntonNextPhase(RaidActor actor)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor)) return false;
            return PrepareAntonNextPhaseLocked();
        }
    }

    bool PrepareAntonNextPhaseLocked()
    {
        if (!HasAntonRewards || current.ClockFaulted || current.State != AntonRaidBattleState.Intermission ||
            current.Phase != 0 || current.AntonPreparing || pendingTransition is not null) return false;
        current = current with { AntonPreparing = true, AntonWaitAnchor = lukeClock.GetTimestamp(),
            AntonWaitSeconds = AntonHeaderSeconds("[start delay time]"), Revision = current.Revision + 1 };
        ScheduleLukeClockLocked();
        return true;
    }

    bool ExpireAntonWaitLocked()
    {
        if (AntonWaitRemainingLocked() is not { } remaining || remaining > TimeSpan.Zero) return false;
        if (!current.AntonPreparing) return PrepareAntonNextPhaseLocked();
        // 86准备检查在线身份，不强制旧Run退场；旧Run保留原phase，只能正常退出。
        if (members.Keys.Any(actor => !actor.IsCurrent))
        {
            current = current with { AntonPreparing = false, AntonWaitSeconds = 0, Revision = current.Revision + 1 };
            return true;
        }
        current = Initialize(1, current.Revision + 1, timerSequence: current.TimerSequence);
        entries.Clear();
        AnchorLukePhaseLocked();
        ScheduleLukeClockLocked();
        return true;
    }
}
