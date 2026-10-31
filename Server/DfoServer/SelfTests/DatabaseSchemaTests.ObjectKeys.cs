using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void ObjectKeyUpgrade(string root)
    {
        foreach (int previousVersion in new[] { 82, 86 }) ObjectKeyUpgrade(root, previousVersion);
    }

    static void ObjectKeyUpgrade(string root, int previousVersion)
    {
        string path = Path.Combine(root, "object-keys-" + previousVersion, "game.db");
        var repository = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("key-schema", () => { });
        var role = repository.Create(owner, new NewCharacter("KeySchema", new(38, 1, 450, 234), null)
            { DungeonState = CharacterDungeonState.NewOrdinary });
        using (var db = Open(path))
        {
            // 六张空表回退到v82的真实键结构，再写旧行；不让当前建库结构冒充迁移输入。
            RewindObjectKeys(db);
            SeedObjectKeyReceipts(db, role.Id, "old-event", new string('a', 32), 258, hasObjectInstance: false);
            RewindRaidLifecycle(db);
            Execute(db, $"UPDATE character_dungeon_state SET next_object_key=65536,last_attempt=258 WHERE character_id={role.Id};PRAGMA user_version={previousVersion}");
            Execute(db, """
                CREATE INDEX key_fixture_index ON character_dungeon_drop_objects(core);
                CREATE TRIGGER key_fixture_guard BEFORE INSERT ON character_dungeon_drop_objects
                  WHEN NEW.gold_amount=99 BEGIN SELECT RAISE(ABORT,'key fixture trigger'); END;
                """);
        }
        string old = Snapshot(path);
        using (var db = Open(path))
        {
            using var tx = db.BeginTransaction();
            DungeonObjectKeySchema.Upgrade(db, tx);
            Reject<SqliteException>(() => SqliteMigrations.Execute(db, tx, "UPDATE character_dungeon_drop_objects SET instance_id='wrong'"),
                "回填后的对象实例外键拒绝错误来源");
            tx.Rollback();
        }
        Check(Snapshot(path) == old, "迁移失败回滚保留旧行、索引、触发器、游标与版本");
        _ = new SqliteGameDatabase(path);
        using (var db = Open(path))
        {
            Check(Query(db, "SELECT instance_id FROM character_dungeon_drop_objects") is string original && original == new string('a', 32) &&
                Query(db, "SELECT next_object_key FROM character_dungeon_state") is 65536L &&
                Query(db, "SELECT last_attempt FROM character_dungeon_state") is 258L,
                $"v{previousVersion}存量对象从原批次回填实例，历史游标与尝试号原样保留");
            Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN ('key_fixture_index','key_fixture_guard')") is 2L,
                "迁移保留自有索引和触发器");
            SeedObjectKeyReceipts(db, role.Id, "new-event", new string('b', 32), 259, hasObjectInstance: true);
            foreach (string table in DungeonObjectKeySchema.Tables)
                Check(Query(db, $"SELECT count(*) FROM {table}") is 2L, "新旧实例复用相同地面key且保留两份账本：" + table);
            Reject<SqliteException>(() => Execute(db, $"""
                INSERT INTO character_dungeon_drop_objects
                  SELECT character_id,event_id,instance_id,1,ground_key,NULL,1,0,0,0
                  FROM character_dungeon_drop_objects WHERE event_id='new-event'
                """), "同实例相同ground key仍不能重复分配");
            Reject<SqliteException>(() => Execute(db, """
                INSERT INTO character_dungeon_drop_objects
                  SELECT character_id,event_id,instance_id,1,513,NULL,99,0,0,0
                  FROM character_dungeon_drop_objects WHERE event_id='new-event'
                """), "迁移后的原触发器仍有效，复制历史行未重放触发器");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "新旧实例账本完整性与外键通过");
        }
        string current = Snapshot(path);
        _ = new SqliteGameDatabase(path);
        Check(Snapshot(path) == current, "新key结构重开不重复迁移、不改写历史数据");
    }

    static void SeedObjectKeyReceipts(SqliteConnection db, ushort id, string eventId, string instance, int attempt, bool hasObjectInstance)
    {
        string objectInstance = hasObjectInstance ? "'" + instance + "'," : "";
        Execute(db, $$"""
            INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,
              monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision)
            VALUES({{id}},'key-schema','{{eventId}}','{{instance}}',0,{{attempt}},1,1,256,1,1,1,'test','test',1,3,1,1,0,1,1);
            INSERT INTO character_dungeon_loot_receipts VALUES({{id}},'key-schema','{{eventId}}','{{instance}}',{{attempt}},1,1,256,'test','test',511,'{}',0,0,0);
            INSERT INTO character_dungeon_pickup_receipts VALUES({{id}},'key-schema','{{eventId}}',511,'{{instance}}',{{attempt}},1,0,121,1,'{}',1);
            INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count)
              VALUES({{id}},'key-schema','{{eventId}}','{{instance}}',0,{{attempt}},1,1,256,'test','test',1);
            INSERT INTO character_dungeon_drop_objects VALUES({{id}},'{{eventId}}',{{objectInstance}}0,512,NULL,1,0,0,0);
            INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
              VALUES({{id}},'key-schema','{{eventId}}',512,'{{instance}}',{{attempt}},1,NULL,NULL,NULL,0,1,1,0,1);
            INSERT INTO character_dungeon_passive_pickups VALUES({{id}},'key-schema',513,'{{instance}}',{{attempt}},1,NULL,1,NULL,NULL,NULL,0,1,1,0,1);
            """);
    }

    static void RewindObjectKeys(SqliteConnection db)
    {
        RemoveSkillTreeExpansionSchema(db);
        bool foreignKeys = Query(db, "PRAGMA foreign_keys") is 1L;
        Execute(db, "PRAGMA foreign_keys=OFF");
        try
        {
            foreach (string table in DungeonObjectKeySchema.Tables)
            {
                if (Query(db, $"SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='{table}'") is not 1L) continue;
                string old = (string)Query(db, $"SELECT sql FROM sqlite_schema WHERE name='{table}'")!;
                string previous = old
                    .Replace("\"character_dungeon_drop_batches\"", "character_dungeon_drop_batches", StringComparison.Ordinal)
                    .Replace("\"character_dungeon_drop_objects\"", "character_dungeon_drop_objects", StringComparison.Ordinal)
                    .Replace("event_id TEXT NOT NULL,instance_id TEXT NOT NULL,ordinal", "event_id TEXT NOT NULL,ordinal", StringComparison.Ordinal)
                    .Replace("UNIQUE(character_id,event_id,instance_id),", "", StringComparison.Ordinal)
                    .Replace("PRIMARY KEY(character_id,instance_id,ground_key)", "PRIMARY KEY(character_id,ground_key)", StringComparison.Ordinal)
                    .Replace("UNIQUE(character_id,instance_id,ground_key)", "UNIQUE(character_id,ground_key)", StringComparison.Ordinal)
                    .Replace("character_id,event_id,instance_id,ground_key", "character_id,event_id,ground_key", StringComparison.Ordinal)
                    .Replace("FOREIGN KEY(character_id,event_id,instance_id) REFERENCES character_dungeon_drop_batches(character_id,event_id,instance_id)",
                        "FOREIGN KEY(character_id,event_id) REFERENCES character_dungeon_drop_batches(character_id,event_id)", StringComparison.Ordinal);
                if (previous == old) continue;
                var columns = ColumnNames(db, table).Where(name => table != "character_dungeon_drop_objects" || name != "\"instance_id\"").ToArray();
                ChangeReceiptDefinition(db, table, _ => previous, columns);
            }
        }
        finally { if (foreignKeys) Execute(db, "PRAGMA foreign_keys=ON"); }
    }
}
