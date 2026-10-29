using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class BeastStageRewardSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_beast_stage_rewards(
            character_id INTEGER NOT NULL,
            account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
            stage INTEGER NOT NULL CHECK(stage BETWEEN 0 AND 5),
            event_id TEXT NOT NULL CHECK(length(event_id)=32),
            map_id INTEGER NOT NULL,
            monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
            item_id INTEGER NOT NULL,
            PRIMARY KEY(character_id,instance_id,stage),
            UNIQUE(character_id,event_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE
        );
        """;

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_beast_stage_rewards'";
        if (command.ExecuteScalar() is not string actual || Normalize(actual) != Normalize(Definition))
            throw new InvalidDataException("魔兽阶段奖励结构或唯一约束不匹配");
        static string Normalize(string sql) => string.Concat(sql.Where(value => !char.IsWhiteSpace(value)))
            .Replace("IFNOTEXISTS", "", StringComparison.Ordinal).TrimEnd(';');
    }
}
