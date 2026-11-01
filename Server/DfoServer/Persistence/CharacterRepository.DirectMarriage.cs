using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Marriage;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public MarriageRecord CommitDirectMarriage(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, MarriageRecord after)
    {
        MarriageRecord Commit()
        {
            if (first.Id == second.Id || after.FirstId != first.Id || after.SecondId != second.Id ||
                after.State != MarriageWeddingRules.Married || after.Wedding is not null || after.MarriedAt != after.CreatedAt)
                throw new InvalidDataException("直接结婚的双方身份或关系状态无效");
            ValidateMarriageChange(null, after);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitDirectMarriage");
            using var tx = write.Transaction;
            var actualFirst = Require(db, tx, firstOwner.Account, first.Name);
            var actualSecond = Require(db, tx, secondOwner.Account, second.Name);
            ValidateMarriageActor(actualFirst, first);
            ValidateMarriageActor(actualSecond, second);
            if (actualFirst.Revision != first.Revision || actualSecond.Revision != second.Revision)
                throw new InvalidOperationException("角色存档版本已变化，请刷新后重试");
            SaveMarriageInTransaction(db, tx, null, after);
            tx.Commit();
            return after;
        }

        if (ReferenceEquals(firstOwner, secondOwner)) return firstOwner.Execute(Commit);
        return AccountOwners.Lease.ExecuteTogether(firstOwner, secondOwner, Commit);
    }
}
