using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BattlePassCeraConsumptionSchema
{
    internal const string StateDefinition = """
        CREATE TABLE account_battle_pass_cera_consumption(
          account TEXT NOT NULL, season_key TEXT NOT NULL,
          total_cera INTEGER NOT NULL DEFAULT 0 CHECK(typeof(total_cera)='integer' AND total_cera>=0),
          numerator_remainder INTEGER NOT NULL DEFAULT 0 CHECK(typeof(numerator_remainder)='integer' AND numerator_remainder BETWEEN 0 AND 99),
          PRIMARY KEY(account,season_key),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;
    internal const string OrderDefinition = """
        CREATE TABLE account_battle_pass_cera_orders(
          account TEXT NOT NULL, season_key TEXT NOT NULL, order_id TEXT NOT NULL CHECK(length(order_id)=32),
          recipient_character_id INTEGER NOT NULL,
          cera INTEGER NOT NULL CHECK(typeof(cera)='integer' AND cera>0),
          eligible_cera INTEGER NOT NULL CHECK(typeof(eligible_cera)='integer' AND eligible_cera BETWEEN 1 AND cera),
          experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>=0),
          percentage INTEGER NOT NULL CHECK(percentage BETWEEN 0 AND 65535),
          before_remainder INTEGER NOT NULL CHECK(before_remainder BETWEEN 0 AND 99),
          resulting_remainder INTEGER NOT NULL CHECK(resulting_remainder BETWEEN 0 AND 99),
          before_season_revision INTEGER NOT NULL CHECK(typeof(before_season_revision)='integer' AND before_season_revision>=0),
          resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>before_season_revision),
          business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
          committed_at INTEGER NOT NULL CHECK(typeof(committed_at)='integer' AND committed_at>=0),
          PRIMARY KEY(account,season_key,order_id),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;
    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        Ensure("account_battle_pass_cera_consumption", StateDefinition);
        Ensure("account_battle_pass_cera_orders", OrderDefinition);
        void Ensure(string table, string definition)
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is not { } actual) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("战令点券消费账结构不兼容");
        }
    }
}
