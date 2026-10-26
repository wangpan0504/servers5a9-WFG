using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DazzlementTowerBindingNotice? DazzlementTowerBindings() => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character ||
            !towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
            !map.Options.Any(option => option.DungeonId == 7601)) return null;
        var period = DazzlementTowerAdmission.At(IncountNow());
        var state = repository.LoadDazzlementTower(owner);
        if (state.Week > period.Week) return null;
        return new DazzlementTowerBindingNotice(period, state.Week == period.Week ? state.Bindings : []);
    });

    public bool CanPublishDazzlementTower(DazzlementTowerBindingNotice notice) =>
        owner.Execute(() => Ready && Selected is not null && notice.Period == DazzlementTowerAdmission.At(IncountNow()));
}
