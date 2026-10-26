using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void TagEvildomUpgrade(string root)
    {
        root = Path.Combine(root, "tag-evildom");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "tag-evildom-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('tag-old',0,'TagOld',7,38,1,450,234,0,123);
                    DROP TABLE account_tag_evildom_configuration;
                    DROP TABLE account_tag_evildom_clearances;
                    PRAGMA user_version=100;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_tag_evildom_configuration(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "再战编队迁移冲突或末段失败时DDL、角色和版本整笔回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM account_tag_evildom_configuration") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "真实v100升级只建空配置表，不臆测旧队伍或改动资产");
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "当前版本重开不重做编队迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE account_tag_evildom_configuration");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
