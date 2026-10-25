namespace DfoServer.Persistence;

internal static class SocialSchema
{
    internal static void Upgrade(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        SqliteMigrations.Execute(db, tx, Sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ")
            .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS "));
    }
    internal static IEnumerable<(string Table, string Sql)> Tables => Sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(sql => sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
        .Select(sql => (sql[13..sql.IndexOf('(')].Trim(), sql.StartsWith("CREATE TABLE account_social_requests(", StringComparison.Ordinal) ? RequestSql : sql));
    const string MemoColumn = "memo TEXT NOT NULL DEFAULT ''";
    const string GroupColumn = "group_id INTEGER NOT NULL DEFAULT 0 CHECK(group_id BETWEEN 0 AND 4)";
    internal static string LegacyRequestSql => Sql.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Single(sql => sql.StartsWith("CREATE TABLE account_social_requests(", StringComparison.Ordinal));
    internal static string RequestSql => LegacyRequestSql.Replace("PRIMARY KEY(source_account,target_account)",
        MemoColumn + "," + GroupColumn + ",PRIMARY KEY(source_account,target_account)", StringComparison.Ordinal);
    internal static void UpgradeRequests(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        const string table = "account_social_requests";
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_social_requests'";
        string actual = (string?)query.ExecuteScalar() ?? throw new InvalidDataException("缺少冒险团好友申请表");
        string normalized = SqliteMigrations.NormalizeTable(actual, table);
        if (normalized == SqliteMigrations.NormalizeTable(RequestSql, table)) return;
        if (normalized != SqliteMigrations.NormalizeTable(LegacyRequestSql, table))
            throw new InvalidDataException("冒险团好友申请迁移拒绝未知结构");
        // 只追加申请者偏好，原申请及时间保持不变。
        SqliteMigrations.Execute(db, tx, "ALTER TABLE " + table + " ADD COLUMN " + MemoColumn);
        SqliteMigrations.Execute(db, tx, "ALTER TABLE " + table + " ADD COLUMN " + GroupColumn);
    }
    internal const string Sql = """
        CREATE TABLE character_social_links(
          owner_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          target_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          kind INTEGER NOT NULL CHECK(kind IN(0,1)),created INTEGER NOT NULL CHECK(created>=0),memo TEXT NOT NULL DEFAULT '',
          PRIMARY KEY(owner_id,target_id,kind),CHECK(owner_id<>target_id)
        ) STRICT;
        CREATE INDEX character_social_target ON character_social_links(target_id,kind);
        CREATE TABLE account_social_links(
          owner_account TEXT NOT NULL REFERENCES accounts(account_id),
          target_account TEXT NOT NULL REFERENCES accounts(account_id),
          created INTEGER NOT NULL CHECK(created>=0),memo TEXT NOT NULL DEFAULT '',group_id INTEGER NOT NULL DEFAULT 0 CHECK(group_id BETWEEN 0 AND 255),
          PRIMARY KEY(owner_account,target_account),CHECK(owner_account<>target_account)
        ) STRICT;
        CREATE TABLE account_social_requests(
          source_account TEXT NOT NULL REFERENCES accounts(account_id),
          target_account TEXT NOT NULL REFERENCES accounts(account_id),
          message TEXT NOT NULL,created INTEGER NOT NULL CHECK(created>=0),
          PRIMARY KEY(source_account,target_account),CHECK(source_account<>target_account)
        ) STRICT;
        """;
}
