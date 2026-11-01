using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void AccountMaterialsUpgrade(string root)
    {
        root = Path.Combine(root, "account-materials"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-table", "wrong-index" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    DROP TABLE account_material_audit;
                    DROP TABLE account_material_balances;
                    DROP INDEX character_raid_rewards_account_time;
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('old-materials',0,'OldMaterials',7,38,1,450,234,0,123);
                    UPDATE characters SET inventory_items_revision=9 WHERE account='old-materials';
                    PRAGMA user_version=172;
                    """);
                if (scenario == "wrong-table") Execute(db, "CREATE TABLE account_material_audit(account TEXT)");
                if (scenario == "wrong-index") Execute(db, "CREATE INDEX character_raid_rewards_account_time ON character_raid_reward_receipts(account)");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "173同名表或索引不兼容回滚全部迁移");
                continue;
            }
            _ = new CharacterRepository(path);
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM account_material_balances") is 0L &&
                    Query(db, "SELECT count(*) FROM character_dungeon_tournament_rewards") is 0L &&
                    Query(db, "SELECT revision||':'||gold||':'||inventory_items_revision FROM characters") is "7:123:9" &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "172升173及重开保留武斗回执表、库存标记和存量，不在无资源SQL迁移中伪造账号余额");
                Execute(db, "INSERT INTO account_wallets(account) VALUES('old-materials')");
                RejectDatabase(() => Execute(db, "INSERT INTO account_material_balances VALUES('old-materials',10308807,-1,0,0,'{}')"));
            }
        }
    }
}
