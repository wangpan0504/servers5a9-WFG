using DfoServer.Game.Adventure;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureCapsuleResult ClaimAdventureCapsule(AdventureCapsuleService service, DateTimeOffset now)
    {
        var capture = owner.Execute(() => (Character: Phase == SessionPhase.Town && CanPlanAdventureRepresentative() ? Selected : null,
            SelectionEpoch, Snapshot: repository.ReadAdventure(owner, service.Catalog, now)));
        if (capture.Character is not { } character) return new(false, 19);
        var prepared = service.Claim(capture.Snapshot, character);
        if (prepared.Plan is not { } plan) return new(false, prepared.Error);
        return owner.Execute(() =>
        {
            if (Phase != SessionPhase.Town || !CanPlanAdventureRepresentative() ||
                !ReferenceEquals(Selected, character) || SelectionEpoch != capture.SelectionEpoch) return new AdventureCapsuleResult(false, 19);
            var committed = repository.CommitAdventureCapsule(owner, plan, service.Catalog, now);
            Selected = committed.Character;
            return new AdventureCapsuleResult(true, 0, committed) { Generation = owner.Generation, Epoch = SelectionEpoch };
        });
    }
    public bool CanPublishAdventureCapsule(AdventureCapsuleResult result) => owner.Execute(() =>
        result.Accepted && Ready && Phase == SessionPhase.Town && result.Generation == owner.Generation &&
        result.Epoch == SelectionEpoch && result.Commit is { } committed && ReferenceEquals(Selected, committed.Character));
}
