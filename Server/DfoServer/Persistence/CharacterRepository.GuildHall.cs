using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildHallStore
{
    public Character? SaveGuildHallPosition(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.SaveGuildHallPosition");
            using var transaction = write.Transaction;
            // 会籍和落点同事务复核；被踢后的旧位置规划不能重新进入大厅。
            var guild = ReadGuildMembership(db, expected.Id, transaction);
            if (guild is null || expected.Guild?.Id != guild.Id) return null;
            var character = SaveInTransaction(db, transaction, owner, expected, position, returnPoint, null) with { Guild = guild };
            transaction.Commit();
            return character;
        });
}
