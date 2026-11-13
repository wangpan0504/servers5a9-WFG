using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class FatigueBatterySchema
{
    internal const string Table = "character_fatigue_battery";
    internal const string Definition = """
        CREATE TABLE character_fatigue_battery(
          character_id INTEGER PRIMARY KEY REFERENCES character_fatigue(character_id) ON DELETE CASCADE,
          charging INTEGER NOT NULL CHECK(typeof(charging)='integer' AND charging BETWEEN 0 AND 32767),
          buff INTEGER NOT NULL CHECK(typeof(buff)='integer' AND buff BETWEEN 0 AND 65535)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", Table);
        if (query.ExecuteScalar() is not string sql) SqliteMigrations.Execute(db, tx, Definition);
        else if (SqliteMigrations.NormalizeTable(sql, Table) != SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("疲劳蓄电池结构不兼容");
    }
}
