using DfoServer.Game.Dungeons;
using DfoServer.Game.Accounts;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Strongway2019;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IStrongway2019MatchingStore
{
    public PartyDungeonEntryCommit[] CommitStrongway2019MatchingEntry(
        IReadOnlyList<PartyDungeonReservation> reservations, InventoryEventPrecondition requiredEvent)
    {
        if (reservations.Count != 8 || reservations.Select(row => row.Expected.Id).Distinct().Count() != 8 ||
            reservations.GroupBy(row => row.Charge.Identity.InstanceId).Count() != 2 ||
            reservations.GroupBy(row => row.Charge.Identity.InstanceId).Any(group => group.Key == Guid.Empty || group.Count() != 4) ||
            requiredEvent.Schedule.EventId != ServerEventService.Strongway2019Id ||
            reservations.Any(row => row.Incount is not null || row.EvildomWar is not null || row.Charge.DungeonId != 5360 ||
                row.Charge.Identity.QuestId != 0 || row.Charge.Weekly is not null ||
                !(row.Charge with { RequiredEvent = null }).IsEmpty ||
                row.Charge.RequiredEvent is { } supplied && supplied != requiredEvent))
            throw new InvalidDataException("首期匹配入场必须是两支四人原实例及同一312活动快照，不能混入费用或其他模式");
        requiredEvent.Schedule.Validate();
        return AccountOwners.Lease.ExecuteRaidGroup(reservations.Select(row => (row.Owner, row.Expected.Id)).ToArray(), 8, () =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitStrongway2019MatchingEntry");
            using var tx = write.Transaction;
            if (!requiredEvent.Matches(ServerEventRepository.Read(db, tx, ServerEventService.Strongway2019Id)))
                throw new InventoryEventRejectedException();
            var result = new PartyDungeonEntryCommit[8];
            var memberships = new DfoServer.Game.Guild.GuildDungeonMembership[8];
            for (int i = 0; i < reservations.Count; i++)
            {
                var row = reservations[i];
                var committed = CommitDungeonEntryInTransaction(db, tx, row.Owner, row.Expected, row.NextObjectKey,
                    row.Charge, out _);
                result[i] = new(committed.Character, []);
                memberships[i] = committed.GuildMembership!;
            }
            for (int i = 0; i < result.Length; i++)
            {
                var instance = reservations[i].Charge.Identity.InstanceId;
                result[i] = result[i] with { GuildMemberships = [.. memberships.Where((_, index) =>
                    reservations[index].Charge.Identity.InstanceId == instance)] };
            }
            tx.Commit();
            return result;
        });
    }
}
