using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BattlePassDeluxeSchema
{
    internal const string Definition = """
        CREATE TABLE account_battle_pass_deluxe_orders(
          account TEXT NOT NULL, season_key TEXT NOT NULL,
          order_id TEXT NOT NULL UNIQUE CHECK(length(order_id)=32),
          commodity_id INTEGER NOT NULL CHECK(commodity_id>0),
          item_id INTEGER NOT NULL CHECK(item_id=490705044),
          cera INTEGER NOT NULL CHECK(typeof(cera)='integer' AND cera BETWEEN 1 AND 2147483647),
          recipient_character_id INTEGER NOT NULL,
          before_season_revision INTEGER NOT NULL CHECK(typeof(before_season_revision)='integer' AND before_season_revision>=0),
          resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>before_season_revision),
          before_total_experience INTEGER NOT NULL CHECK(typeof(before_total_experience)='integer' AND before_total_experience>=0),
          paid_total_experience INTEGER NOT NULL CHECK(typeof(paid_total_experience)='integer' AND paid_total_experience>=0),
          resulting_total_experience INTEGER NOT NULL CHECK(typeof(resulting_total_experience)='integer' AND resulting_total_experience>=paid_total_experience),
          business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
          committed_at INTEGER NOT NULL CHECK(typeof(committed_at)='integer' AND committed_at>=0),
          PRIMARY KEY(account,item_id),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        const string table = "account_battle_pass_deluxe_orders";
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", table);
        if (command.ExecuteScalar() is not { } actual) SqliteMigrations.Execute(db, tx, Definition);
        else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(Definition, table))
            throw new InvalidDataException("战令豪华订单结构不兼容");
    }
}
