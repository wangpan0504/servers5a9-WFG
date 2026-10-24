using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GriefTowerSchema
{
    internal const string Sql = """
        CREATE TABLE account_grief_tower(
          account TEXT PRIMARY KEY,game_day INTEGER NOT NULL CHECK(game_day>0),
          used INTEGER NOT NULL CHECK(used>=0));
        CREATE TABLE character_grief_tower(
          character_id INTEGER PRIMARY KEY,account TEXT NOT NULL,
          game_day INTEGER NOT NULL CHECK(game_day>0),cleared INTEGER NOT NULL CHECK(cleared IN(0,1)),
          layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 101),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        CREATE TABLE character_grief_tower_entries(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),game_day INTEGER NOT NULL CHECK(game_day>0),
          layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 100),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
          invitation INTEGER NOT NULL CHECK(invitation IN(0,1)),account_used INTEGER NOT NULL CHECK(account_used>=0),
          account_limit INTEGER NOT NULL CHECK(account_limit>0),revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,instance_id),UNIQUE(character_id,task_attempt),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx) => Ensure(db, tx, true);
    internal static void Validate(SqliteConnection db, SqliteTransaction tx) => Ensure(db, tx, false);

    internal const string ClearSql = """
        CREATE TABLE character_grief_tower_clears(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          event_id TEXT NOT NULL CHECK(length(event_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          game_day INTEGER NOT NULL CHECK(game_day>0),layer INTEGER NOT NULL CHECK(layer BETWEEN 1 AND 100),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
          mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>0),
          PRIMARY KEY(character_id,instance_id),UNIQUE(character_id,event_id),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_grief_tower_entries(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));
        """;

    internal static void UpgradeClear(SqliteConnection db, SqliteTransaction tx) => Ensure(db,tx,true,ClearSql);
    internal static void ValidateClear(SqliteConnection db, SqliteTransaction tx) => Ensure(db,tx,false,ClearSql);

    static void Ensure(SqliteConnection db, SqliteTransaction tx, bool create, string? statements = null)
    {
        foreach (string sql in (statements ?? Sql).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string name = sql.Split('(')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name";
            query.Parameters.AddWithValue("$name", name);
            if (query.ExecuteScalar() is string existing)
            {
                if (Normalize(existing) != Normalize(sql)) throw new InvalidDataException("未知悲叹账本结构: " + name);
            }
            else if (create) SqliteMigrations.Execute(db, tx, sql);
            else throw new InvalidDataException("悲叹账本缺失: " + name);
        }
    }

    static string Normalize(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c))).TrimEnd(';');
}
