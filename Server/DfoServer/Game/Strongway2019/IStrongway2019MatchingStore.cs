using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Strongway2019;

internal interface IStrongway2019MatchingStore
{
    PartyDungeonEntryCommit[] CommitStrongway2019MatchingEntry(
        IReadOnlyList<PartyDungeonReservation> reservations, InventoryEventPrecondition requiredEvent);
}
