using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildPresenceStore
{
    public void ObserveGuildMemberOnline(AccountOwners.Lease owner, ushort characterId, long observedAtUnix) =>
        owner.Execute(() =>
        {
            if (observedAtUnix <= 0) return false;
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.ObserveGuildMemberOnline");
            using var transaction = write.Transaction;
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            // 会话代次由owner保护；条件更新同时防止账号串读、时钟回拨和晚到观察覆盖新入会记录。
            command.CommandText = """
                UPDATE guild_members SET last_login_unix=$observed
                WHERE character_id=$character AND joined_at_unix <= $observed
                    AND last_login_unix < $observed
                    AND EXISTS(SELECT 1 FROM characters c
                        WHERE c.id=guild_members.character_id AND c.account=$account)
                """;
            command.Parameters.AddWithValue("$observed", observedAtUnix);
            command.Parameters.AddWithValue("$character", characterId);
            command.Parameters.AddWithValue("$account", owner.Account);
            bool changed = command.ExecuteNonQuery() != 0;
            transaction.Commit();
            return changed;
        });
}
