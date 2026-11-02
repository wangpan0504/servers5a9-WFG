using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BattlePassSchema
{
    internal const string SeasonDefinition = """
        CREATE TABLE account_battle_pass_seasons(
          account TEXT NOT NULL,
          season_key TEXT NOT NULL CHECK(length(trim(season_key))>0),
          total_experience INTEGER NOT NULL DEFAULT 0 CHECK(typeof(total_experience)='integer' AND total_experience>=0),
          pass_type INTEGER NOT NULL DEFAULT 0 CHECK(typeof(pass_type)='integer' AND pass_type BETWEEN 0 AND 255),
          revision INTEGER NOT NULL DEFAULT 0 CHECK(typeof(revision)='integer' AND revision>=0),
          PRIMARY KEY(account,season_key)
        );
        """;
    internal const string ClaimDefinition = """
        CREATE TABLE account_battle_pass_claims(
          account TEXT NOT NULL,
          season_key TEXT NOT NULL,
          level INTEGER NOT NULL CHECK(typeof(level)='integer' AND level BETWEEN 1 AND 80),
          track INTEGER NOT NULL CHECK(typeof(track)='integer' AND track BETWEEN 0 AND 1),
          recipient_character_id INTEGER NOT NULL,
          PRIMARY KEY(account,season_key,level,track),
          FOREIGN KEY(account,season_key) REFERENCES account_battle_pass_seasons(account,season_key)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        Ensure("account_battle_pass_seasons", SeasonDefinition);
        Ensure("account_battle_pass_claims", ClaimDefinition);

        void Ensure(string table, string definition)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() is not { } actual) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("Battle-pass schema is incompatible: " + table);
        }
    }
}
