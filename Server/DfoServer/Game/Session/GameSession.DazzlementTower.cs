using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DazzlementTowerNotice? CaptureDazzlementTower(bool onlyForSelection = false) => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        if (onlyForSelection && (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
            area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
            !map.Options.Any(option => option.DungeonId == 7601))) return null;
        var period = DazzlementTowerAdmission.At(IncountNow());
        var state = repository.LoadDazzlementTower(owner);
        if (state.Week > period.Week) return null;
        return new DazzlementTowerNotice(owner.Generation, SelectionEpoch, character,
            new(period, state.Week == period.Week ? state.Bindings : []), state.UsedToday(character.Id, period));
    });

    public bool CanPublishDazzlementTower(DazzlementTowerNotice notice) => owner.Execute(() =>
        owner.Generation == notice.Generation && SelectionEpoch == notice.Epoch && ReferenceEquals(Selected, notice.Character) &&
        notice.Binding.Period == DazzlementTowerAdmission.At(IncountNow()));
}
