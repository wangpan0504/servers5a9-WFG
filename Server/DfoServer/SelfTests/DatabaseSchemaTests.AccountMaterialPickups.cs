using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void AccountMaterialPickupsUpgrade(string root)
    {
        root = Path.Combine(root, "account-material-pickups"); Directory.CreateDirectory(root);
        const string table = "character_dungeon_drop_pickups";
        string previous = DungeonFrozenLootSchema.Definition(DungeonObjectKeySchema.Definition(
            DungeonDropCollectionSchema.PickupDefinition, table), table);
        foreach (string scenario in new[] { "ordinary", "wrong-column", "unknown-column", "wrong-check" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                using (var tx = db.BeginTransaction())
                {
                    using var query = db.CreateCommand(); query.Transaction = tx;
                    query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_dungeon_drop_pickups'";
                    string current = (string)query.ExecuteScalar()!;
                    int start = current.IndexOf("CHECK((account_material IS NULL", StringComparison.Ordinal);
                    int end = current.IndexOf("PRIMARY KEY(", start, StringComparison.Ordinal);
                    int oldStart = previous.IndexOf("CHECK((epic_piece IS NULL", StringComparison.Ordinal);
                    int oldEnd = previous.IndexOf("PRIMARY KEY(", oldStart, StringComparison.Ordinal);
                    SqliteMigrations.ReplaceTableConstraint(db, tx, table, current[start..end].TrimEnd(), previous[oldStart..oldEnd].TrimEnd());
                    tx.Commit();
                }
                Execute(db, """
                    ALTER TABLE character_dungeon_drop_pickups DROP COLUMN account_material;
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                      VALUES('old-pickups',0,'OldPickups',7,38,1,450,234,0,123);
                    INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
                      map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,
                      before_level,after_level,before_experience,after_experience,revision)
                      SELECT id,account,'old-event','old-instance',0,1,1,76126,20,1,1,1,'legacy','legacy',1,3,1,1,0,1,1 FROM characters;
                    INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
                      map_id,monster_key,rule_id,resource_source,object_count)
                      SELECT id,account,'old-event','old-instance',0,1,1,76126,20,'legacy','legacy',2 FROM characters;
                    INSERT INTO character_dungeon_drop_objects(character_id,event_id,instance_id,ordinal,ground_key,core,gold_amount,x,y,record_slot)
                      SELECT id,'old-event','old-instance',0,400,'{}',0,100,100,0 FROM characters;
                    INSERT INTO character_dungeon_drop_objects(character_id,event_id,instance_id,ordinal,ground_key,core,gold_amount,x,y,record_slot)
                      SELECT id,'old-event','old-instance',1,401,'{}',0,100,100,0 FROM characters;
                    INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
                      destination_slot,item_uid,after_core,amount,revision)
                      SELECT id,account,'old-event',400,'old-instance',1,1,121,2147483649,'{}',3,7 FROM characters;
                    CREATE INDEX pickup_legacy_index ON character_dungeon_drop_pickups(item_uid);
                    PRAGMA user_version=173;
                    """);
                if (scenario == "wrong-column") Execute(db, "ALTER TABLE character_dungeon_drop_pickups ADD COLUMN account_material INTEGER");
                if (scenario == "unknown-column") Execute(db, "ALTER TABLE character_dungeon_drop_pickups ADD COLUMN unknown_asset TEXT");
                if (scenario == "wrong-check")
                {
                    using var tx = db.BeginTransaction();
                    SqliteMigrations.ReplaceTableConstraint(db, tx, table, "item_uid>0", "item_uid>=0");
                    tx.Commit();
                }
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "174未知列或旧资产约束拒绝，新增列、改约束与版本全部回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT item_uid FROM character_dungeon_drop_pickups") is 2147483649L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='pickup_legacy_index'") is 1L &&
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "173升174及重开保留旧64位UID、来源、索引、角色资产和格式");
                const string valid = "{\"ItemId\":10308807,\"Amount\":3,\"Balance\":10,\"WalletRevision\":2}";
                RejectDatabase(() => Execute(db, "UPDATE character_dungeon_drop_pickups SET account_material='" + valid + "'"));
                Execute(db, """
                    INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
                      amount,revision,receipt_format,account_material)
                      SELECT id,account,'old-event',401,'old-instance',1,1,3,8,1,
                      '{"ItemId":10308807,"Amount":3,"Balance":10,"WalletRevision":2}' FROM characters;
                    """);
                foreach (string invalid in new[] { "{}", "[]", "bad", valid.Replace("\"Amount\":3", "\"Amount\":2", StringComparison.Ordinal),
                    valid.Replace("\"Balance\":10", "\"Balance\":2", StringComparison.Ordinal),
                    valid.Replace("\"WalletRevision\":2", "\"WalletRevision\":0", StringComparison.Ordinal) })
                    RejectDatabase(() => Execute(db, "UPDATE character_dungeon_drop_pickups SET account_material='" + invalid + "' WHERE ground_key=401"));
                RejectDatabase(() => Execute(db, "UPDATE character_dungeon_drop_pickups SET destination_list=0 WHERE ground_key=401"));
                RejectDatabase(() => Execute(db, "UPDATE character_dungeon_drop_pickups SET receipt_format=0 WHERE ground_key=401"));
                Check(Query(db, "SELECT count(*) FROM character_dungeon_drop_pickups") is 2L,
                    "钱包收据只接受完整正增量和已提交版本，与实体、货币、图鉴互斥");
            }
            _ = new CharacterRepository(path);
        }
    }
}
