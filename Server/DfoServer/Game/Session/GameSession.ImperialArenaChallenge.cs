using DfoServer.Game.ImperialArena;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public ImperialArenaChallengePreparation? PrepareImperialArenaChallenge(IImperialArenaChallengeStore store,
        ImperialArenaCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && ChannelEntry?.Type == 39 && Selected is { Level: >= 90 } selected &&
        !selected.IsPvpPrivate && !HasPendingTownQuestCommit && pendingRental is null
            ? new ImperialArenaChallengePreparation(selected, owner.Generation, SelectionEpoch,
                store.ReadImperialArenaChallenge(owner, catalog, ImperialArenaPeriod.At(now))) : null);

    public ImperialArenaChallengeCommit? CommitImperialArenaChallenge(IImperialArenaChallengeStore store, ImperialArenaCatalog catalog,
        ImperialArenaChallengeClaim claim) => owner.Execute(() =>
        CanPublishImperialArenaChallenge(claim) && ImperialArenaPeriod.At((timeProvider ?? TimeProvider.System).GetUtcNow()) == claim.Challenge.Period &&
        !HasPendingTownQuestCommit && pendingRental is null ? store.ClaimImperialArenaChallenge(owner, catalog, claim) : null);

    public bool CanPublishImperialArenaChallenge(ImperialArenaChallengeClaim claim) => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && ChannelEntry?.Type == 39 && owner.Generation == claim.Generation &&
        SelectionEpoch == claim.Epoch && ReferenceEquals(Selected, claim.Character));
}
