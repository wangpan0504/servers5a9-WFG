using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed record GmAccountRow(string Account, bool Disabled, long Revision, int Characters);
internal sealed record GmCharacterRow(int Id, int Slot, string Name, int Job, int GrowType, int Level, long Gold, long Revision);

// GM 站点的只读账号目录：只执行 SELECT，不取账号 owner，因此不会顶掉在线玩家；不读取口令字段。
internal sealed class GmAccountDirectory(string path)
{
    readonly SqliteGameDatabase database = new(path);

    public GmAccountRow[] ListAccounts()
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT a.account_id,a.disabled,a.revision,(SELECT count(*) FROM characters c WHERE c.account=a.account_id)
            FROM accounts a ORDER BY a.account_id
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<GmAccountRow>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt64(2), reader.GetInt32(3)));
        return [.. rows];
    }

    public GmCharacterRow[]? ListCharacters(string account)
    {
        using var db = database.Open();
        using var exists = db.CreateCommand();
        exists.CommandText = "SELECT 1 FROM accounts WHERE account_id=$account";
        exists.Parameters.AddWithValue("$account", account);
        if (exists.ExecuteScalar() is null)
            return null;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,slot,name,job,grow_type,level,gold,revision FROM characters WHERE account=$account ORDER BY slot";
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = cmd.ExecuteReader();
        var rows = new List<GmCharacterRow>();
        while (reader.Read())
            rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.GetInt32(5), reader.GetInt64(6), reader.GetInt64(7)));
        return [.. rows];
    }
}