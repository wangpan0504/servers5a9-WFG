using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class FatigueDailyResetSchema
{
    internal const string Table = "character_fatigue_reset_days";
    internal const string Definition = """
        CREATE TABLE character_fatigue_reset_days(
          character_id INTEGER PRIMARY KEY REFERENCES character_fatigue(character_id) ON DELETE CASCADE,
          service_day INTEGER NOT NULL CHECK(typeof(service_day)='integer' AND service_day BETWEEN 1 AND 3652058)
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
            throw new InvalidDataException("疲劳服务日结构不兼容");
    }
}
