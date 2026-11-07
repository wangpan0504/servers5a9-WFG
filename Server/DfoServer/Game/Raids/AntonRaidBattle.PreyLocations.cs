namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    internal AntonRaidBattleChange? ReportPreyTownLocation(RaidActor actor, byte area)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || !actor.IsCurrent || !members.ContainsKey(actor) ||
                area is < 2 or > 5 || actor.Session.Selected?.Position is not { Town: 59 } position || position.Area != area ||
                current.PreyMemberLocations.GetValueOrDefault(actor.Character) == area) return null;
            return UpdatePreyMemberLocations(current.PreyMemberLocations.SetItem(actor.Character, area));
        }
    }

    void InstallPreyRunLocations(RaidDungeonRun run, PreyLandingCandidate? landing)
    {
        if (Kind != RaidKind.Prey || landing is null) return;
        var locations = current.PreyMemberLocations;
        foreach (var row in run.Actors)
            locations = locations.SetItem(row.Actor.Character, checked((ushort)landing.Arrival.FromArea));
        _ = UpdatePreyMemberLocations(locations);
    }

    AntonRaidBattleChange UpdatePreyMemberLocations(System.Collections.Immutable.ImmutableDictionary<ushort, ushort> locations)
    {
        var before = current;
        current = before with { PreyMemberLocations = locations, Revision = before.Revision + 1 };
        // 位置是已提交城镇/Run的展示事实；等待退场的奖励候选也须保留，不能恢复旧左侧位置。
        if (pendingTransition is { } pending) pendingTransition = pending with { PreyMemberLocations = locations };
        return new(before, current);
    }
}
