using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class ServerItemUsageSchema
{
    internal const string StateTable = "character_server_item_usage";
    internal const string AccountTable = "account_server_item_refills";
    internal const string StateDefinition = """
        CREATE TABLE character_server_item_usage(
          character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
          state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object')
        );
        """;
    internal const string AccountDefinition = """
        CREATE TABLE account_server_item_refills(
          account TEXT NOT NULL,item_id INTEGER NOT NULL CHECK(item_id>0),
          service_day INTEGER NOT NULL CHECK(service_day BETWEEN 1 AND 3652058),
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          completed INTEGER NOT NULL CHECK(completed IN (0,1)),
          PRIMARY KEY(account,item_id)
        );
        """;
    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, definition) in new[] { (StateTable, StateDefinition), (AccountTable, AccountDefinition) })
        {
            using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name";
            query.Parameters.AddWithValue("$name", table);
            if (query.ExecuteScalar() is not string sql) SqliteMigrations.Execute(db, tx, definition);
            else if (SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("全局物品周期结构不兼容");
        }
    }
}
