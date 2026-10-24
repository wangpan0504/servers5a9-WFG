using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DungeonSpecialEntrySchema
{
    internal const string Sql = """
        CREATE TABLE dungeon_special_entry_receipts(
            character_id INTEGER NOT NULL, account TEXT NOT NULL,
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*'),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
            service_day INTEGER NOT NULL CHECK(service_day BETWEEN 1 AND 3652058),
            kind INTEGER NOT NULL CHECK(kind BETWEEN 1 AND 3),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
            layer INTEGER NOT NULL CHECK((kind=3 AND layer BETWEEN 1 AND 100) OR (kind<>3 AND layer=0)),
            gold INTEGER NOT NULL CHECK(gold BETWEEN 0 AND 2147483647),
            revision INTEGER NOT NULL CHECK(revision>0),
            PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,task_attempt)
        );
        CREATE INDEX dungeon_special_entry_day ON dungeon_special_entry_receipts(account,kind,service_day,character_id);
        CREATE TABLE dungeon_altar_clear_receipts(
            character_id INTEGER NOT NULL, account TEXT NOT NULL, instance_id TEXT NOT NULL,
            event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0), epoch INTEGER NOT NULL CHECK(epoch>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN (11006,11007)),
            completed_rounds INTEGER NOT NULL CHECK(completed_rounds BETWEEN 0 AND maximum_rounds),
            maximum_rounds INTEGER NOT NULL CHECK((dungeon_id=11006 AND maximum_rounds=15) OR (dungeon_id=11007 AND maximum_rounds=7)),
            play_milliseconds INTEGER NOT NULL CHECK(play_milliseconds BETWEEN 0 AND 4294967295),
            cleared INTEGER NOT NULL CHECK(cleared IN (0,1) AND (cleared=0 OR completed_rounds=maximum_rounds)),
            death_events TEXT NOT NULL, reward_json TEXT NOT NULL,
            gained_experience INTEGER NOT NULL CHECK(gained_experience BETWEEN 0 AND 1000000),
            proficiency_gained INTEGER NOT NULL CHECK(proficiency_gained BETWEEN 0 AND 1000000),
            PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,event_id),
            FOREIGN KEY(character_id,instance_id) REFERENCES dungeon_special_entry_receipts(character_id,instance_id)
        );
        CREATE INDEX dungeon_altar_best ON dungeon_altar_clear_receipts(account,character_id,dungeon_id,completed_rounds DESC,play_milliseconds);
        CREATE TABLE dungeon_despair_progress(
            character_id INTEGER NOT NULL PRIMARY KEY, account TEXT NOT NULL,
            next_layer INTEGER NOT NULL CHECK(next_layer BETWEEN 1 AND 100),
            last_clear_day INTEGER NOT NULL CHECK(last_clear_day BETWEEN 0 AND 3652058),
            clear_count INTEGER NOT NULL CHECK(clear_count>=0)
        );
        CREATE TABLE dungeon_despair_clear_receipts(
            character_id INTEGER NOT NULL, account TEXT NOT NULL, instance_id TEXT NOT NULL,
            event_id TEXT NOT NULL CHECK(length(event_id)=32 AND event_id NOT GLOB '*[^0-9a-f]*'),
            task_attempt INTEGER NOT NULL CHECK(task_attempt>0), epoch INTEGER NOT NULL CHECK(epoch>0),
            dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 11008 AND 11107),
            clear_day INTEGER NOT NULL CHECK(clear_day BETWEEN 1 AND 3652058),
            play_milliseconds INTEGER NOT NULL CHECK(play_milliseconds BETWEEN 0 AND 4294967295),
            death_events TEXT NOT NULL, assets_json TEXT NOT NULL,
            PRIMARY KEY(character_id,instance_id), UNIQUE(character_id,event_id),
            FOREIGN KEY(character_id,instance_id) REFERENCES dungeon_special_entry_receipts(character_id,instance_id)
        );
        """;

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        string[] statements = Sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] names = ["dungeon_special_entry_receipts", "dungeon_special_entry_day", "dungeon_altar_clear_receipts", "dungeon_altar_best", "dungeon_despair_progress", "dungeon_despair_clear_receipts"];
        for (int index = 0; index < statements.Length; index++)
        {
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE name=$name";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$name", names[index]);
            if (command.ExecuteScalar() is string existing)
            {
                // 回退版本夹具保留后续表时，只接受精确相同的已知定义，不吞掉未知同名结构。
                static string Normalize(string sql) => string.Concat(sql.Where(ch => !char.IsWhiteSpace(ch))).TrimEnd(';').ToLowerInvariant();
                if (Normalize(existing) != Normalize(statements[index])) throw new InvalidDataException("未知塔与祭坛入场账本结构");
                continue;
            }
            command.Parameters.Clear();
            command.CommandText = statements[index];
            command.ExecuteNonQuery();
        }
    }
}
