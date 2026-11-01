using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void InventoryItemRevisionUpgrade(string root)
    {
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
}
