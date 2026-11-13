namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal AntonRaidBattleChange? SetPreyMarker(RaidActor actor, byte group, int target)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || !actor.IsCurrent || !members.ContainsKey(actor) ||
                current.State != AntonRaidBattleState.Fighting || current.ClockFaulted || current.PhaseExpired ||
                pendingTransition is not null || LukePhaseDueLocked() || group > 2 || target is < 0 or > 320 ||
                target is > 0 and < 301 || target > 0 && !AllowsPreyMarkerTarget(target) ||
                current.PreyMarkers.GetValueOrDefault(group) == target) return null;
            var before = current;
            var markers = target == 0 ? before.PreyMarkers.Remove(group) : before.PreyMarkers.SetItem(group, target);
            CommitBattleCandidate(before, before with { PreyMarkers = markers, Revision = before.Revision + 1 });
            return new(before, current);
        }
    }

    bool AllowsPreyMarkerTarget(int target)
    {
        if (current.Phase == 0 ? target is < 301 or > 316 : target is < 317 or > 320) return false;
        string? symbol = script.Symbols.FirstOrDefault(row => row.Value == target).Key;
        return symbol is not null && current.Symbols.TryGetValue(symbol, out int dungeon) && dungeon > 0 &&
            script.Phases[current.Phase].Dungeons.Any(row => row.Id == dungeon) &&
            current.Dungeons.GetValueOrDefault(dungeon) == GameWorld.Raids.RaidDungeonState.Open;
    }
}
