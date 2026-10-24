using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    PreparedDungeonAltarSettlement? pendingAltarSettlement, committedAltarSettlement;

    internal int AltarMaximumRounds => allRooms.Sum(room => room.Altar?.Plan.Definition.RoundCount ?? 0);
    internal int AltarCompletedRounds => CompletedAltarRounds + (current.Altar?.Round ?? 0);
    internal bool HasCommittedAltarSettlement => committedAltarSettlement is not null;

    public PreparedDungeonAltarSettlement? PrepareAltarSettlement(Character expected, DungeonAltarRewardPlan reward,
        InventoryPlan inventory, uint playMilliseconds, bool failed = false)
    {
        reward.Validate();
        if ((pendingAltarSettlement ?? committedAltarSettlement) is { } original)
            return ReferenceEquals(original.Reward, reward) && ReferenceEquals(original.Inventory, inventory) &&
                ReferenceEquals(original.Expected, expected) && original.Fact.PlayMilliseconds == playMilliseconds &&
                original.Fact.Cleared == !failed ? original : null;
        if (entry.DungeonId is not (11006 or 11007) || expected.Id != identity.CharacterId ||
            allRooms.Any(room => room.Altar is null) || reward.MaximumRounds != AltarMaximumRounds ||
            reward.CompletedRounds != AltarCompletedRounds || inventory.Reason != "altar_clear_reward" ||
            !ReferenceEquals(inventory.Before, expected.Inventory) || HasPendingDeath || pendingSettlement is not null ||
            (failed ? Phase != SinglePlayerDungeonPhase.Dungeon || ClearFact is not null :
                Phase != SinglePlayerDungeonPhase.Settlement || ClearFact is null || reward.CompletedRounds != reward.MaximumRounds))
            return null;
        var fact = new DungeonAltarCompletionFact(identity, entry.DungeonId, AltarCompletedRounds, AltarMaximumRounds,
            playMilliseconds, !failed, deaths.Keys.Order().ToImmutableArray());
        pendingAltarSettlement = new(fact, reward, inventory, expected);
        if (failed) Phase = SinglePlayerDungeonPhase.Settlement;
        return pendingAltarSettlement;
    }

    public bool IsPendingAltarSettlement(PreparedDungeonAltarSettlement prepared) =>
        ReferenceEquals(pendingAltarSettlement, prepared) && Phase == SinglePlayerDungeonPhase.Settlement && prepared.Fact.Identity == identity;

    public bool ConfirmCommittedAltarSettlement(PreparedDungeonAltarSettlement prepared)
    {
        if (ReferenceEquals(committedAltarSettlement, prepared)) return true;
        if (!IsPendingAltarSettlement(prepared)) return false;
        committedAltarSettlement = prepared;
        pendingAltarSettlement = null;
        return true;
    }
}
