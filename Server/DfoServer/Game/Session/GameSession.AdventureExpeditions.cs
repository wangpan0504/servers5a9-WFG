using DfoServer.Game.Adventure;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public AdventureExpeditionResult PlanAdventureExpedition(AdventureExpeditionService service,
        Func<AdventureSnapshot, (AdventureExpeditionPlan? Plan, ushort Error, uint Reward)> prepare, DateTimeOffset now)
    {
        var capture = owner.Execute(() => (Character: CanPlanAdventureRepresentative() ? Selected : null, SelectionEpoch, Phase,
            Identity: formalDungeon?.Run.Snapshot.Identity, Snapshot: repository.ReadAdventure(owner, service.Catalog, now)));
        if (capture.Character is not { } character) return new(false, 3);
        var prepared = prepare(capture.Snapshot);
        if (prepared.Plan is not { } plan) return new(false, prepared.Error);
        return owner.Execute(() =>
        {
            if (!CanPlanAdventureRepresentative() || !ReferenceEquals(Selected, character) || SelectionEpoch != capture.SelectionEpoch ||
                Phase != capture.Phase || formalDungeon?.Run.Snapshot.Identity != capture.Identity) return new AdventureExpeditionResult(false, 3);
            var snapshot = repository.CommitAdventureExpedition(owner, plan with { ActorId = character.Id }, service.Catalog, now);
            return new AdventureExpeditionResult(true, 0, snapshot, prepared.Reward)
                { Selected = character, Generation = owner.Generation, Epoch = SelectionEpoch, Phase = Phase, Dungeon = capture.Identity };
        });
    }
    public bool CanPublishAdventureExpedition(AdventureExpeditionResult result) => owner.Execute(() =>
        result.Accepted && Ready && result.Generation == owner.Generation && result.Epoch == SelectionEpoch && result.Phase == Phase &&
        result.Dungeon == formalDungeon?.Run.Snapshot.Identity && ReferenceEquals(result.Selected, Selected));
}
