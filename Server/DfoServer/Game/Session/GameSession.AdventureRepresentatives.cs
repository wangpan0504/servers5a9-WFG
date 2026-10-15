using DfoServer.Game.Adventure;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureRepresentativeResult SetAdventureRepresentative(AdventureRepresentativeService service,
        byte job, byte grow, ushort? id, DateTimeOffset now)
    {
        var capture = owner.Execute(() => (Character: CanPlanAdventureRepresentative() ? Selected : null, SelectionEpoch, Phase,
            Identity: formalDungeon?.Run.Snapshot.Identity, Snapshot: repository.ReadAdventure(owner, service.Catalog, now)));
        if (capture.Character is not { } character) return new(false, 3);
        if (service.Busy(capture.Snapshot, job, grow)) return new(false, 227);
        var plan = service.Prepare(capture.Snapshot, character.Id, job, grow, id);
        if (plan is null) return new(false, 3);
        return owner.Execute(() =>
        {
            if (!CanPlanAdventureRepresentative() || !ReferenceEquals(Selected, character) || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase || formalDungeon?.Run.Snapshot.Identity != capture.Identity) return new AdventureRepresentativeResult(false, 3);
            var snapshot = repository.CommitAdventureRepresentative(owner, plan, service.Catalog, now);
            return new AdventureRepresentativeResult(true, 0, snapshot)
                { Selected = character, Generation = owner.Generation, Epoch = SelectionEpoch, Phase = Phase, Dungeon = capture.Identity };
        });
    }
    public bool CanPublishAdventureRepresentative(AdventureRepresentativeResult result) => owner.Execute(() =>
        result.Accepted && Ready && result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        result.Dungeon == formalDungeon?.Run.Snapshot.Identity && ReferenceEquals(result.Selected, Selected));
    bool CanPlanAdventureRepresentative() => pendingRental is null && pendingCommunication is null && Ready && Selected is not null && !HasPendingQuestScene &&
        pendingQuestCompletion is null && pendingQuestLifecycle is null && pendingQuestQuickClear is null &&
        !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard && formalDungeon?.Ground.HasPendingOperation != true &&
        formalDungeon?.HasPendingFatigue != true && formalDungeon?.Run.HasPendingDeath != true && formalDungeon?.Run.HasPendingSettlement != true &&
        Phase is SessionPhase.Town or SessionPhase.Dungeon or SessionPhase.Settlement;
}
