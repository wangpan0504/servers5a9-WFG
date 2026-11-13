using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed record PreyLandingIdentity(Guid Instance, int Phase, long Generation, int FromArea);
internal sealed record PreyLandingCandidate(PreyLandingIdentity Arrival, int Dungeon, byte Side);

internal sealed partial class AntonRaidBattle
{
    internal PreyLandingIdentity? CapturePreyLandingArrival(AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || view.Instance != Instance || view.Phase != current.Phase ||
                current.State != AntonRaidBattleState.Fighting || current.PhaseExpired || current.ClockFaulted ||
                pendingTransition is not null || TerminalRequested(current) || LukePhaseDueLocked() ||
                current.Symbols.GetValueOrDefault("[prepare raid]") != 1) return null;
            var timer = new RaidTimer(current.Phase + 1, 1000);
            long generation = current.TimerGenerations.GetValueOrDefault(timer);
            int from = current.Symbols.GetValueOrDefault("[slenikon from area]");
            int first = current.Phase == 0 ? 301 : 317, last = current.Phase == 0 ? 316 : 320;
            if (generation <= 0 || !current.Timers.ContainsKey(timer) || from < first || from > last ||
                view.TimerGenerations.GetValueOrDefault(timer) != generation ||
                view.Symbols.GetValueOrDefault("[slenikon from area]") != from) return null;
            return new(Instance, current.Phase, generation, from);
        }
    }

    internal PreyLandingCandidate? CapturePreyLanding(AntonRaidBattleView view, byte side)
    {
        lock (gate)
        {
            if (side is not (4 or 5) || CapturePreyLandingArrival(view) is not { } arrival) return null;
            int dungeon = current.Symbols.GetValueOrDefault($"[area_{arrival.FromArea - 300}]");
            if (!AllowsPreyEntryFromSide(dungeon, side) || !AllowsPreyEntry(dungeon)) return null;
            return new(arrival, dungeon, side);
        }
    }

    internal bool AllowsPreyEntryFromSide(int dungeon, byte side)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || side is not (4 or 5)) return false;
            var definition = script.Phases[current.Phase].Dungeons.SingleOrDefault(row => row.Id == dungeon);
            // 一阶段右侧防御，二阶段右侧本体；其余据点均从船尾左侧降落。
            return definition is not null && (PreyDungeonType(definition) == (current.Phase == 0 ? 5 : 6)) == (side == 5);
        }
    }

    internal bool IsPreyLandingCurrent(PreyLandingCandidate landing)
    {
        lock (gate) return CapturePreyLanding(current, landing.Side) == landing;
    }
}
