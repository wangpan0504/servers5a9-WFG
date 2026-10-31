using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 显式本服兼容策略：资源原值按秒解释，不声称还原原服单位、休整或死亡机制。
internal sealed record LukeRaidPhaseTimeoutPolicy(string Source, ImmutableArray<int> Seconds)
{
    internal static LukeRaidPhaseTimeoutPolicy Shared95 { get; } = new("95jp/luke/phase-limits-v1", [2700, 3600])
        { ResourceSeconds = [2100, 2400] };
    internal ImmutableArray<int> ResourceSeconds { get; init; }
    internal bool Valid(int phases) => !string.IsNullOrWhiteSpace(Source) && !Seconds.IsDefaultOrEmpty &&
        Seconds.Length == phases && Seconds.All(value => value is > 0 and <= 86400);

    internal bool Matches(RaidScript script) => Valid(script.Phases.Length) &&
        script.Headers.Count(header => header.Tag == "[phase time over]") == 1 &&
        script.Headers.Single(header => header.Tag == "[phase time over]").Values
            .Select(value => value.Number).SequenceEqual(ResourceSeconds.IsDefault ? Seconds : ResourceSeconds);
}

internal sealed partial class AntonRaidBattle
{
    readonly LukeRaidPhaseTimeoutPolicy? lukePhaseTimeout;
    long? lukePhaseAnchor;
    int lukeAnchoredPhase = -1;

    void AnchorLukePhaseLocked()
    {
        if (!lukeClockActive || lukePhaseTimeout is null || current.State != AntonRaidBattleState.Fighting ||
            lukeAnchoredPhase == current.Phase) return;
        lukeAnchoredPhase = current.Phase;
        if (Kind != RaidKind.Prey || lukePhaseAnchor is null) lukePhaseAnchor = lukeClock.GetTimestamp();
        current = current with { PhaseClockGeneration = checked(current.Phase + 1L) };
    }

    TimeSpan? LukePhaseRemainingLocked()
    {
        // 逻辑成功已封存后不再超时，慢SQL或退场不能把已赢阶段倒退为失败。
        if (lukePhaseTimeout is null || lukePhaseAnchor is not { } anchor ||
            lukeAnchoredPhase != current.Phase || pendingTransition is not null || TerminalRequested(current)) return null;
        var remaining = TimeSpan.FromSeconds(lukePhaseTimeout.Seconds[current.Phase]) - lukeClock.GetElapsedTime(anchor);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    bool LukePhaseDueLocked() => LukePhaseRemainingLocked() is { } remaining && remaining <= TimeSpan.Zero;

    // 资源FAIL与本服deadline共用可发布的终结事实，原退场协调者负责逐人SQL与安全回城。
    static AntonRaidBattleView FailLukePhase(AntonRaidBattleView state) => state with
    {
        PhaseExpired = true,
        Dungeons = state.Dungeons.ToImmutableDictionary(row => row.Key, _ => RaidDungeonState.Hide),
        Timers = ImmutableDictionary<RaidTimer, int>.Empty,
        TimerAnchors = ImmutableDictionary<RaidTimer, long>.Empty,
        TimerGenerations = ImmutableDictionary<RaidTimer, long>.Empty,
        Deferred = ImmutableDictionary<int, RaidClause>.Empty,
        DeferredAnchors = ImmutableDictionary<int, long>.Empty,
        DeferredGenerations = ImmutableDictionary<int, long>.Empty
    };

    internal void FaultExpiredLukePhase(Guid instance)
    {
        lock (gate)
            if (instance == Instance && current.PhaseExpired && !current.ClockFaulted)
                current = current with { ClockFaulted = true, Revision = current.Revision + 1 };
    }

    internal bool LukePhaseOpen(int phase)
    {
        lock (gate) return Kind == RaidKind.Luke && current.Phase == phase &&
            pendingTransition is null && !TerminalRequested(current) && !LukePhaseDueLocked();
    }

    internal uint? LukePhaseSeconds(Guid instance, int phase, long generation)
    {
        lock (gate)
        {
            if (instance != Instance || current.Phase != phase || generation == 0 ||
                current.PhaseClockGeneration != generation || LukePhaseRemainingLocked() is not { } remaining) return null;
            return checked((uint)Math.Ceiling(remaining.TotalSeconds));
        }
    }
}
