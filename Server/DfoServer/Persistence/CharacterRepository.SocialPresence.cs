using DfoServer.Game.Accounts;
using DfoServer.Game.Social;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ISocialPresenceStore
{
    public void ObserveSocialAccountOnline(AccountOwners.Lease owner, long observedAtUnix) => owner.Execute(() =>
    {
        if (observedAtUnix <= 0) return false;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ObserveSocialAccountOnline");
        using var command = db.CreateCommand();
        command.Transaction = write.Transaction;
        command.CommandText = """
            INSERT INTO account_social_presence(account,last_seen_unix) VALUES($account,$observed)
            ON CONFLICT(account) DO UPDATE SET last_seen_unix=max(last_seen_unix,excluded.last_seen_unix)
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$observed", observedAtUnix);
        bool changed = command.ExecuteNonQuery() != 0;
        write.Transaction.Commit();
        return changed;
    });
}
