using DfoServer.Persistence;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void InventoryItemRevisionUpgrade(string root)
    {
        AccountCommon45Upgrade(root);
        root = Path.Combine(root, "inventory-item-revisions");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "column-conflict", "trigger-conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('items-old',0,'ItemsOld',7,38,1,450,234,0,123)");
                foreach (var (name, _) in InventoryItemRevisionSchema.Triggers()) Execute(db, "DROP TRIGGER " + name);
                Execute(db, "ALTER TABLE characters DROP COLUMN inventory_items_revision; ALTER TABLE account_cargo DROP COLUMN inventory_items_revision; PRAGMA user_version=170");
                if (scenario == "column-conflict") Execute(db, "ALTER TABLE characters ADD COLUMN inventory_items_revision TEXT DEFAULT 'unknown'");
                if (scenario == "trigger-conflict")
                    Execute(db, "CREATE TRIGGER inventory_version_character_inventory_items_insert AFTER INSERT ON character_inventory_items BEGIN SELECT 1; END");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            string before = Snapshot(path);
            if (scenario != "upgrade")
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "库存标记升级冲突及末段失败回滚列、触发器、版本和原资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT revision||':'||gold||':'||inventory_items_revision FROM characters") is "7:123:0",
                    "170升级只补库存标记和触发器，不改变已有资产与角色业务revision");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "重开库不重置库存标记");
            using (var db = Open(path)) Execute(db, "DROP TRIGGER inventory_version_character_inventory_items_update");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }

    // 历史夹具先撤回空容器45；不能丢弃真实共享物品来伪造旧版本。
    static void RewindAccountCommon45(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='account_common45_slot'") is 0L) return;
        if (Query(db, "SELECT count(*) FROM character_inventory_slots WHERE list_type=45") is not 0L)
            throw new InvalidOperationException("旧库夹具不能丢弃账号公共物品");
        using var tx = db.BeginTransaction();
        SqliteMigrations.Execute(db, tx, "DROP INDEX account_common45_slot");
        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_inventory_slots",
            "CHECK(list_type IN (0,1,2,3,7,12,38,45,46))", "CHECK(list_type IN (0,1,2,3,7,12,38,46))");
        SqliteMigrations.ReplaceTableConstraint(db, tx, "character_inventory_slots",
            "CHECK((list_type=12 AND character_id IS NULL AND slot_index<240) OR (list_type=45 AND character_id IS NULL AND slot_index<50) OR (list_type NOT IN(12,45) AND character_id IS NOT NULL AND (list_type<>2 OR slot_index<264)))", "CHECK((list_type=12 AND character_id IS NULL AND slot_index<240) OR (list_type<>12 AND character_id IS NOT NULL AND (list_type<>2 OR slot_index<264)))");
        tx.Commit();
    }

    static void AccountCommon45Upgrade(string root)
    {
        root = Path.Combine(root, "account-common45");
        Directory.CreateDirectory(root);
        string core = JsonSerializer.Serialize(new ItemCore(490013716, ItemKind.Material, 7) { Extension97 = 1234, Attr = 32 });
        foreach (string scenario in new[] { "upgrade", "index-conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RewindAccountCommon45(db);
                Execute(db, "PRAGMA user_version=197; INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('common-old',0,'CommonOld',7,38,1,450,234,0,123)");
                Execute(db, $"INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(7001,1,'common-old','{core}')");
                Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'common-old',0,145,7001)");
                if (scenario == "index-conflict")
                    Execute(db, "CREATE UNIQUE INDEX account_common45_slot ON character_inventory_slots(account,slot_index) WHERE list_type=12");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            string before = Snapshot(path);
            if (scenario != "upgrade")
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "45迁移索引冲突及末段失败回滚全部DDL、版本、自增和旧资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT item_core FROM character_inventory_items WHERE item_uid=7001") is string savedCore && savedCore == core &&
                    Query(db, "SELECT character_id||':'||list_type||':'||slot_index||':'||item_uid FROM character_inventory_slots") is "1:0:145:7001" &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "197升级只开放45，不迁移旧Main材料、不改UID、核心、位置、金币或角色revision");
                for (int uid = 7002; uid <= 7006; uid++)
                    Execute(db, $"INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES({uid},NULL,'common-old','{core}')");
                Execute(db, "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(NULL,'common-old',45,0,7002),(NULL,'common-old',45,49,7003),(NULL,'common-old',12,0,7004)");
                Check(Query(db, "SELECT count(*) FROM character_inventory_slots WHERE character_id IS NULL") is 3L,
                    "45的0和49槽合法，账号仓库12与45可使用同一格号而不互相挤占");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(() => Execute(db,
                    "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(NULL,'common-old',45,50,7005)"), "45拒绝第51格");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(() => Execute(db,
                    "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(NULL,'common-old',45,0,7005)"), "45拒绝同账号重复槽位");
                Execute(db, $"INSERT INTO character_inventory_items(item_uid,character_id,account,item_core) VALUES(7007,1,'common-old','{core}')");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(() => Execute(db,
                    "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(1,'common-old',45,1,7007)"), "45拒绝角色owner");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "45迁移后原外键与数据库完整性保持");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "45迁移重开幂等，不改资产与自增高水位");
            using (var db = Open(path)) Execute(db, "DROP INDEX account_common45_slot");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
