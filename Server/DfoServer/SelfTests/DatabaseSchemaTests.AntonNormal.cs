using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void AntonNormalUpgrade(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "conflict", "late-failure", "newer" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('anton-old',0,'AntonOld',7,38,1,450,234,0,123)");
                Execute(db, "DROP TABLE character_anton_normal_progress; PRAGMA user_version=165");
                if (scenario == "conflict") Execute(db, "CREATE TABLE character_anton_normal_progress(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            string before = Snapshot(path);
            if (scenario != "upgrade")
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "安徒恩迁移冲突、末段失败及较新版本拒绝保留原库");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_anton_normal_progress") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "165升级建空安徒恩日进度，保留资产");
                Execute(db, "INSERT INTO character_anton_normal_progress SELECT id,account,20261031,3,4 FROM characters");
                foreach (string invalid in new[] { "branch=0", "branch=5", "progress=7", "progress=1.5", "account='other'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db, "UPDATE character_anton_normal_progress SET " + invalid), "安徒恩进度约束与账号归属");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "重开保留安徒恩分支与通关位");
        }
    }
}
