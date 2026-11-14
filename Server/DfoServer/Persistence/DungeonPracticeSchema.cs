using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DungeonPracticeSchema
{
    internal const string Day = "practice_day INTEGER NOT NULL DEFAULT 0 CHECK(practice_day BETWEEN 0 AND 3652058)";
    internal const string Used = "practice_used INTEGER NOT NULL DEFAULT 0 CHECK(practice_used BETWEEN 0 AND 3 AND (practice_day>0 OR practice_used=0))";

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var definition in new[] { Day, Used })
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT count(*) FROM pragma_table_info('character_dungeon_state') WHERE name=$name";
            command.Parameters.AddWithValue("$name", definition[..definition.IndexOf(' ')]);
            if (command.ExecuteScalar() is 0L)
                SqliteMigrations.Execute(db, tx, "ALTER TABLE character_dungeon_state ADD COLUMN " + definition);
        }
        Validate(db, tx);
    }

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_state'";
        string sql = command.ExecuteScalar() as string ?? throw new InvalidDataException("练习次数缺少原副本owner表");
        string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        foreach (var definition in new[] { Day, Used })
            if (!Compact(sql).Contains(Compact(definition), StringComparison.Ordinal))
                throw new InvalidDataException("练习次数列类型、默认值或约束不兼容");
    }
}
