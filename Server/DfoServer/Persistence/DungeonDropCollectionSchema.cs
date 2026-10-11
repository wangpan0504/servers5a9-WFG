using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// v19完整集合账本使用原数据库与外层事务；接管旧掉落分支v18完整结构，不补发历史资产。
internal static class DungeonDropCollectionSchema
{
    public const string Definition = """
        CREATE TABLE character_dungeon_drop_batches(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,quest_id INTEGER NOT NULL CHECK(quest_id>=0),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          map_id INTEGER NOT NULL,monster_key INTEGER NOT NULL CHECK(monster_key BETWEEN 1 AND 65535),
          rule_id TEXT NOT NULL,resource_source TEXT NOT NULL,object_count INTEGER NOT NULL CHECK(object_count BETWEEN 0 AND 500),
          PRIMARY KEY(character_id,event_id),UNIQUE(character_id,instance_id,monster_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_growth_receipts(character_id,event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        CREATE TABLE character_dungeon_drop_objects(
          character_id INTEGER NOT NULL,event_id TEXT NOT NULL,ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 499),
          ground_key INTEGER NOT NULL CHECK(ground_key BETWEEN 1 AND 65535),
          core TEXT CHECK(core IS NULL OR (json_valid(core) AND json_type(core)='object')),
          gold_amount INTEGER NOT NULL CHECK(gold_amount BETWEEN 0 AND 2147483647),
          x INTEGER NOT NULL CHECK(x BETWEEN 0 AND 65535),y INTEGER NOT NULL CHECK(y BETWEEN 0 AND 65535),
          record_slot INTEGER NOT NULL CHECK(record_slot BETWEEN -32768 AND 32767),
          CHECK((core IS NULL AND gold_amount>0) OR (core IS NOT NULL AND gold_amount=0)),
          PRIMARY KEY(character_id,ground_key),UNIQUE(character_id,event_id,ordinal),UNIQUE(character_id,event_id,ground_key),
          FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_drop_batches(character_id,event_id));
        CREATE TABLE character_dungeon_drop_pickups(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,event_id TEXT NOT NULL,ground_key INTEGER NOT NULL,
          instance_id TEXT NOT NULL,task_attempt INTEGER NOT NULL CHECK(task_attempt>0),epoch INTEGER NOT NULL CHECK(epoch>0),
          destination_slot INTEGER,item_uid INTEGER,after_core TEXT,
          currency_kind INTEGER,amount INTEGER NOT NULL CHECK(amount>0),after_balance INTEGER,account_revision INTEGER,
          revision INTEGER NOT NULL CHECK(revision>0),
          CHECK((destination_slot IS NOT NULL AND item_uid IS NOT NULL AND destination_slot BETWEEN 0 AND 351 AND item_uid BETWEEN 1 AND 2147483647 AND
            after_core IS NOT NULL AND json_valid(after_core) AND json_type(after_core)='object' AND currency_kind IS NULL AND
            after_balance IS NULL AND account_revision IS NULL) OR
            (destination_slot IS NULL AND item_uid IS NULL AND after_core IS NULL AND currency_kind IS NOT NULL AND currency_kind BETWEEN 0 AND 18 AND
            after_balance IS NOT NULL AND after_balance>=0 AND account_revision IS NOT NULL AND account_revision>=0)),
          PRIMARY KEY(character_id,ground_key),
          FOREIGN KEY(character_id,event_id,ground_key) REFERENCES character_dungeon_drop_objects(character_id,event_id,ground_key),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        if (InstalledTables(db, tx) != 0)
        {
            Validate(db, tx, required: true);
            return;
        }
        foreach (string sql in Definition.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            SqliteMigrations.Execute(db, tx, sql);
        Validate(db, tx, required: true);
    }

    public static void Validate(SqliteConnection db, SqliteTransaction tx, bool required = false)
    {
        int count = InstalledTables(db, tx);
        if (count == 0 && !required) return;
        if (count != 3)
            throw new InvalidDataException("地面掉落集合需要完整批次、对象与拾取三表");
        using var query = db.CreateCommand();
        query.Transaction = tx;
        foreach (string sql in RequiredColumns)
        {
            query.CommandText = sql;
            using var reader = query.ExecuteReader();
        }
    }

    static int InstalledTables(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """
            SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN
              ('character_dungeon_drop_batches','character_dungeon_drop_objects','character_dungeon_drop_pickups')
            """;
        return Convert.ToInt32(query.ExecuteScalar());
    }

    private static readonly string[] RequiredColumns =
    [
        "SELECT character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count FROM character_dungeon_drop_batches LIMIT 0",
        "SELECT character_id,event_id,ordinal,ground_key,core,gold_amount,x,y,record_slot FROM character_dungeon_drop_objects LIMIT 0",
        "SELECT character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision FROM character_dungeon_drop_pickups LIMIT 0",
    ];
}
