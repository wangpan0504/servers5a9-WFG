using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IFiendwarAreaTravelStore
{
    public Character[] CommitFiendwarAreaTravel(IReadOnlyList<FiendwarAreaTravelReservation> reservations,
        Func<bool> current) => AccountOwners.Lease.ExecuteGroup(
        reservations.Select(row => (row.Actor.Owner, row.Actor.Character)).ToArray(), () =>
        {
            if (!current() || reservations.Any(row => !row.Actor.Session.MatchesFiendwarAreaTravel(row)))
                return [];
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitFiendwarAreaTravel");
            using var tx = write.Transaction;
            var committed = reservations.Select(row => SaveInTransaction(db, tx, row.Actor.Owner,
                row.Expected, row.Plan.Position!, row.Plan.Return, null)).ToArray();
            // 定时结束或原预留失效时全员回滚，不能只保存了前半队。
            if (!current()) return [];
            tx.Commit();
            return committed;
        });
}
