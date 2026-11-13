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

    byte[]? lastStatusSnapshot;
    public byte[]? LastStatusSnapshot => owner.Execute(() => lastStatusSnapshot?.ToArray());

    public bool RecordCharacterStatusSnapshot(ReadOnlySpan<byte> body)
    {
        if (body.Length != 111) return false;
        byte[] copy = body.ToArray();
        return owner.Execute(() =>
        {
            if (Selected is null) return false;
            lastStatusSnapshot = copy;
            return true;
        });
    }

    public (bool Accepted, ushort Error, DfoServer.Game.Characters.Character? Character, long Generation, long Epoch) ClaimCharacterGrowthCapsule(
        uint requiredExperience = 10_000_000) => owner.Execute<(bool, ushort, DfoServer.Game.Characters.Character?, long, long)>(() =>
    {
        if (Phase != SessionPhase.Town || Selected is not { } character || character.DungeonState is not { } state)
            return (false, (ushort)19, null, 0L, 0L);
        if (state.GrowthCapsuleExperience < requiredExperience)
            return (false, (ushort)204, null, 0L, 0L);
        var committed = repository.ClaimGrowthCapsule(owner, character, requiredExperience);
        Selected = committed;
        return (true, (ushort)0, committed, owner.Generation, SelectionEpoch);
    });
}
