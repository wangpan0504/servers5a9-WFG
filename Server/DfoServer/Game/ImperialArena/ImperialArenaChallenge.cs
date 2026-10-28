using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.ImperialArena;

internal static class ImperialArenaRankingPolicy
{
    internal static bool Eligible(ImperialArenaPeriod admitted, int minimumMilliseconds, long elapsedMilliseconds, DateTimeOffset completed) =>
        minimumMilliseconds > 0 && elapsedMilliseconds >= minimumMilliseconds && elapsedMilliseconds <= uint.MaxValue &&
        ImperialArenaPeriod.At(completed).Week == admitted.Week && !ImperialArenaPeriod.RankingClosed(completed);
}

internal sealed record ImperialArenaChallenge(ImperialArenaPeriod Period, Guid InstanceId, uint CurrentMilliseconds,
    ImmutableArray<byte> Team, uint TargetMilliseconds, bool Claimable, uint PreviousRank)
{
    public bool Matches(ImperialArenaChallenge other) => Period == other.Period && InstanceId == other.InstanceId &&
        CurrentMilliseconds == other.CurrentMilliseconds && Team.SequenceEqual(other.Team) && TargetMilliseconds == other.TargetMilliseconds &&
        Claimable == other.Claimable && PreviousRank == other.PreviousRank;
}

internal sealed record ImperialArenaChallengeClaim(Character Character, long Generation, long Epoch,
    ImperialArenaChallenge Challenge, InventoryItem Item, long Created);
internal sealed record ImperialArenaChallengePreparation(Character Character, long Generation, long Epoch, ImperialArenaChallenge Challenge);
internal sealed record ImperialArenaChallengeCommit(ImperialArenaChallengeClaim Claim, uint MailId, bool Duplicate);

internal interface IImperialArenaChallengeStore
{
    ImperialArenaChallenge ReadImperialArenaChallenge(AccountOwners.Lease owner, ImperialArenaCatalog catalog, ImperialArenaPeriod period);
    ImperialArenaChallengeCommit ClaimImperialArenaChallenge(AccountOwners.Lease owner, ImperialArenaCatalog catalog, ImperialArenaChallengeClaim claim);
}
