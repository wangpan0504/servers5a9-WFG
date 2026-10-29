using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BeastDungeonReservationSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_beast_dungeon_reservations(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            attempt INTEGER NOT NULL CHECK(attempt>0),
            entry_epoch INTEGER NOT NULL CHECK(entry_epoch>0),
            day INTEGER NOT NULL CHECK(day>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 310 AND 314),
            expected_used INTEGER NOT NULL CHECK(expected_used BETWEEN 0 AND 1),
            state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2),
            PRIMARY KEY(character_id,instance_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        CREATE UNIQUE INDEX IF NOT EXISTS beast_pending_character ON character_beast_dungeon_reservations(character_id) WHERE state=0;
        """;

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        var statements = Definition.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var (name, expected) in new[] { ("character_beast_dungeon_reservations", statements[0]), ("beast_pending_character", statements[1]) })
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
            command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteScalar() is not string actual ||
                Normalize(actual) != Normalize(expected))
                throw new InvalidDataException("魔兽预约结构或唯一约束不匹配: " + name);
        }
        static string Normalize(string sql) => string.Concat(sql.Where(value => !char.IsWhiteSpace(value)))
            .Replace("IFNOTEXISTS", "", StringComparison.Ordinal).TrimEnd(';');
    }
}
