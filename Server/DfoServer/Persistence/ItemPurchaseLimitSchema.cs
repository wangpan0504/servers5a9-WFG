using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class ItemPurchaseLimitSchema
{
    internal const string Sql = """
        CREATE TABLE item_purchase_counts(
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL CHECK(character_id BETWEEN 0 AND 65535),
            item_id INTEGER NOT NULL CHECK(item_id>0),
            period INTEGER NOT NULL CHECK(period BETWEEN 1 AND 4),
            day INTEGER NOT NULL CHECK((period=4 AND day=0) OR (period BETWEEN 1 AND 3 AND day BETWEEN 1 AND 3652058)),
            count INTEGER NOT NULL CHECK(count BETWEEN 1 AND 2147483647),
            PRIMARY KEY(account,character_id,item_id,period)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx) =>
        SqliteMigrations.Execute(db, tx, Sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
}
