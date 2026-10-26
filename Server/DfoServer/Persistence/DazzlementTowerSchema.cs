using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DazzlementTowerSchema
{
    internal const string Table = "account_dazzlement_tower_bindings";
    internal const string Definition = """
        CREATE TABLE account_dazzlement_tower_bindings(
          account TEXT NOT NULL,
          slot INTEGER NOT NULL CHECK(typeof(slot)='integer' AND slot BETWEEN 1 AND 5),
          week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week BETWEEN 10000101 AND 99991231),
          character_id INTEGER UNIQUE REFERENCES characters(id) ON DELETE SET NULL,
          last_entry_day INTEGER NOT NULL CHECK(typeof(last_entry_day)='integer' AND last_entry_day>=0),
          PRIMARY KEY(account,slot)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", Table);
        var actual = query.ExecuteScalar();
        if (actual is null) SqliteMigrations.Execute(db, tx, Definition);
        else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, Table) != SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("眩惑之塔绑定结构不兼容");
    }
}
