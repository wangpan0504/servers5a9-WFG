using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IRaidStartStore
{
    public ImmutableArray<Character> CommitRaidStart(Guid instance, IReadOnlyList<RaidStartReservation> reservations) =>
        AccountOwners.Lease.ExecuteRaid(reservations.Select(r => (r.Actor.Owner, r.Expected.Id)).ToArray(), () =>
        {
            if (instance == Guid.Empty || reservations.Any(r => !r.Actor.Session.MatchesRaidStart(r)))
                throw new InvalidOperationException("攻坚开战成员快照已变化");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitRaidStart");
            using var tx = write.Transaction;
            var committed = ImmutableArray.CreateBuilder<Character>(reservations.Count);
            foreach (var r in reservations)
                committed.Add(SaveInTransaction(db, tx, r.Actor.Owner, r.Expected, r.Position, r.Expected.Return,
                    new(r.Inventory.Before, r.Inventory.After, "anton_raid_start:" + instance.ToString("N"))));
            tx.Commit();
            return committed.ToImmutable();
        });
}
