using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PcRoomSchema
{
    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        // 旧验证库已保存领取事实和燃烧选择；只接管相同结构，不重建表或清空记录。
        foreach (var (table, definition) in new[]
        {
            ("account_pcroom_claims", Definition.Split(';')[0]),
            ("character_pcroom_claims", Definition.Split(';')[1]),
            ("character_fatigue_acceleration", FatigueAccelerationSchema.Definition)
        })
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not string actual)
                SqliteMigrations.Execute(db, tx, definition);
            else if (SqliteMigrations.NormalizeTable(actual, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("网吧领取或燃烧选择表结构不兼容：" + table);
        }
    }

    internal const string Definition = """
        CREATE TABLE account_pcroom_claims(
            account TEXT PRIMARY KEY REFERENCES accounts(account_id) ON DELETE CASCADE,
            claim_day INTEGER NOT NULL CHECK(typeof(claim_day)='integer' AND claim_day>=0)
        );
        CREATE TABLE character_pcroom_claims(
            character_id INTEGER PRIMARY KEY,
            account TEXT NOT NULL,
            claim_day INTEGER NOT NULL CHECK(typeof(claim_day)='integer' AND claim_day>=0),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;
}
