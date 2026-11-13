using DfoServer.Game.Accounts;

namespace DfoServer.Persistence;

internal sealed partial class ClientSettingsRepository
{
    public bool SaveSeenNotice(AccountOwners.Lease owner, byte id) => owner.Execute(() =>
    {
        if (id >= 128) throw new InvalidDataException("已看提示编号无效");
        using var db = database.Open();
        using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT 1 FROM account_seen_notices WHERE account=$account AND notice_id=$id";
            query.Parameters.AddWithValue("$account", owner.Account);
            query.Parameters.AddWithValue("$id", id);
            if (query.ExecuteScalar() is not null) return false;
        }
        using var write = db.BeginWrite("ClientSettingsRepository.SaveSeenNotice");
        using var tx = write.Transaction;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "INSERT INTO account_seen_notices(account,notice_id) VALUES($account,$id) ON CONFLICT DO NOTHING";
        update.Parameters.AddWithValue("$account", owner.Account);
        update.Parameters.AddWithValue("$id", id);
        bool changed = update.ExecuteNonQuery() == 1;
        tx.Commit();
        return changed;
    });
}
