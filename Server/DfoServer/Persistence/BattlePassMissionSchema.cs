using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BattlePassMissionSchema
{
    internal const string Rules = """
        CREATE TABLE battle_pass_season_rules(
          season_key TEXT PRIMARY KEY NOT NULL CHECK(length(trim(season_key))>0),
          start_utc INTEGER NOT NULL CHECK(typeof(start_utc)='integer' AND start_utc>=0),
          end_utc INTEGER NOT NULL CHECK(typeof(end_utc)='integer' AND end_utc>start_utc),
          rules_json TEXT NOT NULL
        );
        """;
    internal const string Progress = """
        CREATE TABLE account_battle_pass_mission_progress(
          account TEXT NOT NULL, season_key TEXT NOT NULL,
          category INTEGER NOT NULL CHECK(typeof(category)='integer' AND category BETWEEN 0 AND 2),
          period_key INTEGER NOT NULL CHECK(typeof(period_key)='integer' AND period_key>=0),
          mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 65535),
          current_count INTEGER NOT NULL CHECK(typeof(current_count)='integer' AND current_count>=0),
          PRIMARY KEY(account,season_key,category,period_key,mission_id),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;
    internal const string Claims = """
        CREATE TABLE account_battle_pass_mission_claims(
          account TEXT NOT NULL, season_key TEXT NOT NULL,
          category INTEGER NOT NULL CHECK(typeof(category)='integer' AND category BETWEEN 0 AND 2),
          period_key INTEGER NOT NULL CHECK(typeof(period_key)='integer' AND period_key>=0),
          mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 65535),
          experience INTEGER NOT NULL CHECK(typeof(experience)='integer' AND experience>0),
          claimed_at INTEGER NOT NULL CHECK(typeof(claimed_at)='integer' AND claimed_at>=0),
          resulting_season_revision INTEGER NOT NULL CHECK(typeof(resulting_season_revision)='integer' AND resulting_season_revision>0),
          PRIMARY KEY(account,season_key,category,period_key,mission_id),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;
    internal const string Sources = """
        CREATE TABLE account_battle_pass_source_events(
          account TEXT NOT NULL, source_kind TEXT NOT NULL, source_id TEXT NOT NULL CHECK(length(source_id)=32),
          season_key TEXT NOT NULL, occurred_at INTEGER NOT NULL CHECK(typeof(occurred_at)='integer' AND occurred_at>=0),
          PRIMARY KEY(account,source_kind,source_id),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        Ensure("battle_pass_season_rules", Rules);
        Ensure("account_battle_pass_mission_progress", Progress);
        Ensure("account_battle_pass_mission_claims", Claims);
        Ensure("account_battle_pass_source_events", Sources);
        void Ensure(string table, string definition)
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is not { } actual) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("战令任务表结构不兼容：" + table);
        }
    }
}
