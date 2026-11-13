using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class MonsterMockBattleSchema
{
    internal const string Definition = """
        CREATE TABLE account_monster_mock_battle(
          account TEXT PRIMARY KEY,
          revision INTEGER NOT NULL CHECK(revision>0),
          display_name TEXT NOT NULL,
          lineup_stars INTEGER NOT NULL CHECK(lineup_stars BETWEEN 0 AND 25),
          state TEXT NOT NULL CHECK(json_valid(state) AND json_type(state)='object')
        ) STRICT;
        CREATE INDEX account_mock_battle_matching ON account_monster_mock_battle(lineup_stars,account);
        CREATE INDEX account_mock_battle_name ON account_monster_mock_battle(display_name);
        CREATE TABLE account_monster_mock_battle_runs(
          run_id TEXT PRIMARY KEY CHECK(length(run_id)=32),
          account TEXT NOT NULL,
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          world_id INTEGER NOT NULL CHECK(world_id>=0),
          opened_at INTEGER NOT NULL CHECK(opened_at>=0),
          paid_revision INTEGER NOT NULL CHECK(paid_revision>0),
          state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2)
        ) STRICT;
        CREATE UNIQUE INDEX account_mock_battle_active ON account_monster_mock_battle_runs(account) WHERE state=0;
        """;

    static readonly (string Type, string Name)[] Objects =
    [
        ("table", "account_monster_mock_battle"), ("index", "account_mock_battle_matching"),
        ("index", "account_mock_battle_name"), ("table", "account_monster_mock_battle_runs"),
        ("index", "account_mock_battle_active")
    ];

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx) => Check(db, tx, create: true);
    public static void Validate(SqliteConnection db, SqliteTransaction tx) => Check(db, tx, create: false);

    static void Check(SqliteConnection db, SqliteTransaction tx, bool create)
    {
        var statements = Definition.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < Objects.Length; index++)
        {
            var row = Objects[index];
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type=$type AND name=$name";
            command.Parameters.AddWithValue("$type", row.Type);
            command.Parameters.AddWithValue("$name", row.Name);
            var actual = command.ExecuteScalar();
            if (actual is null && create) SqliteMigrations.Execute(db, tx, statements[index]);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, row.Name) !=
                SqliteMigrations.NormalizeTable(statements[index], row.Name))
                throw new InvalidDataException("怪物模拟战存储结构不兼容：" + row.Name);
        }
    }
}
