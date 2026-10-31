using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 只对完整卢克流程启用，不改变诊断合成脚本的立即转阶段合同。
internal sealed record LukeRaidFlowPolicy(string Source, int BreakSeconds, int StartDelaySeconds)
{
    internal static LukeRaidFlowPolicy Shared95 { get; } = new("95jp/luke/flow-v1", 600, 3);
    internal bool Matches(RaidScript script) => !string.IsNullOrWhiteSpace(Source) &&
        script.Headers.Count(row => row.Tag == "[phase break time]" && row.Values is [{ Number: 600 }]) == 1 &&
        script.Headers.Count(row => row.Tag == "[start delay time]" && row.Values is [{ Number: 3 }]) == 1 &&
        script.Headers.Count(row => row.Tag == "[movie end time]") == 1 &&
        script.Headers.Single(row => row.Tag == "[movie end time]").Values is { IsEmpty: false } movie &&
        movie.All(row => row.Number is > 0 and <= 65) &&
        BreakSeconds == 600 && StartDelaySeconds == 3;
}

internal sealed partial class AntonRaidBattle
{
    readonly LukeRaidFlowPolicy? lukeFlow;

    TimeSpan? LukeWaitRemainingLocked() => lukeFlow is not null && !current.ClockFaulted &&
        current.State == AntonRaidBattleState.Intermission
        ? TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(current.LukeWaitSeconds) -
            lukeClock.GetElapsedTime(current.LukeWaitAnchor)).Ticks)) : null;

    internal uint? LukeWaitSeconds(AntonRaidBattleView expected)
    {
        lock (gate) return ReferenceEquals(current, expected) && LukeWaitRemainingLocked() is { } remaining
            ? checked((uint)Math.Ceiling(remaining.TotalSeconds)) : null;
    }

    // 本人Handler已持命令门禁；时钟则尝试全团门禁。两者共用全员加载/候战门禁。
    internal bool PrepareLukeNextPhase(RaidActor? requester = null)
    {
        RaidActor[] actors;
        AntonRaidBattleView expected;
        lock (gate)
        {
            expected = current;
            if (lukeFlow is null || expected.State != AntonRaidBattleState.Intermission || expected.Phase != 0 ||
                expected.ClockFaulted || pendingTransition is not null || dungeonRuns.Count != 0 ||
                lukeRevivalReservations.Count != 0 || !AllRewardsConfirmed(0) ||
                requester is not null && !members.ContainsKey(requester) ||
                requester is null && LukeWaitRemainingLocked() != TimeSpan.Zero) return false;
            actors = members.Keys.ToArray();
        }
        if (actors.Length == 0) return false;
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var command in actors.Where(actor => actor != requester).Select(actor => actor.Session.CommandGate).Distinct())
            {
                if (!command.Wait(0)) return false;
                held.Add(command);
            }
            return RaidOwnerGroup.Execute(RaidKind.Luke, actors.Select(actor => (actor.Owner, actor.Character)).ToArray(), () =>
            {
                if (actors.Any(actor => !actor.IsCurrent || !actor.Session.CanPrepareLukeNextPhase(actor, this))) return false;
                lock (gate)
                {
                    if (!ReferenceEquals(current, expected) || actors.Length != members.Count ||
                        actors.Any(actor => !members.ContainsKey(actor))) return false;
                    if (!expected.LukePreparing)
                        current = expected with { LukePreparing = true, LukeWaitSeconds = lukeFlow.StartDelaySeconds,
                            LukeWaitAnchor = lukeClock.GetTimestamp(), Revision = expected.Revision + 1 };
                    else
                    {
                        if (LukeWaitRemainingLocked() != TimeSpan.Zero) return false;
                        current = ReconcileLukeRoutes(Initialize(1, expected.Revision + 1, expected.Symbols, expected.TimerSequence));
                        entries.Clear();
                        AnchorLukePhaseLocked();
                    }
                    ScheduleLukeClockLocked();
                    return true;
                }
            });
        }
        catch (InvalidOperationException) when (actors.Any(actor => !actor.IsCurrent)) { return false; }
        finally { foreach (var command in held.AsEnumerable().Reverse()) command.Release(); }
    }
}
