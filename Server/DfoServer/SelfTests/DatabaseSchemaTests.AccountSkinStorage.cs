using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void AccountSkinStorageUpgrade(string root)
    {
        root = Path.Combine(root, "account-skins");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "account-skins-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES(1,'skin-old',0,'SkinOld',7,38,1,450,234,0,123);
                    DROP TABLE account_skin_storage;
                    PRAGMA user_version=143;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_skin_storage(incompatible INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "账号皮肤仓迁移冲突及末段失败回滚版本和原资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM account_skin_storage") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "143升级只新增空账号皮肤仓，保留角色资产且不补造账号皮肤");
                Execute(db, "INSERT INTO account_skin_storage VALUES('skin-old','{\"Owned\":[],\"Emoticons\":[{\"SkinId\":410000046,\"ExpiresAt\":0}]}')");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "重开库不改变已保存账号皮肤仓");
            using (var db = Open(path)) Execute(db, "DROP TABLE account_skin_storage");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
