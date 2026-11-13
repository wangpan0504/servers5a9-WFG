using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void ReturningAdventurerUpgrade(string root)
    {
        root = Path.Combine(root, "returning-adventurer-upgrade");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "conflict", "late-failure", "newer" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('return-old',0,'ReturnOld',7,38,1,450,234,0,777)");
                Execute(db, "DROP TABLE account_returning_mission_progress; DROP TABLE account_returning_reward_claims; PRAGMA user_version=204");
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_returning_reward_claims(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            string before = Snapshot(path);
            if (scenario != "upgrade")
            {
                RejectDatabase(() => new CharacterRepository(path));
                That(Snapshot(path) == before, "回归账本迁移冲突、末段失败及较新版本拒绝保留原库：" + scenario);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                That(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT (SELECT count(*) FROM account_returning_mission_progress)+(SELECT count(*) FROM account_returning_reward_claims)") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:777",
                    "v204只追加空回归账本，不改角色资产或补造任务和领取事实");
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            That(Snapshot(path) == saved, "回归账本迁移重开幂等，保留全部旧角色数据");
        }
        foreach (string scenario in new[] { "upstream205", "local205", "partial205", "unknown-rename205", "late205" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('return-205',0,'Return205',7,38,1,450,234,0,777)");
                Execute(db, "INSERT INTO character_rename_pending VALUES(1,'return-205',12345)");
                if (scenario == "upstream205")
                    Execute(db, "DROP TABLE account_returning_mission_progress; DROP TABLE account_returning_reward_claims");
                else
                {
                    Execute(db, "INSERT INTO account_returning_mission_progress VALUES('return-205',2000000000,1,7)");
                    Execute(db, "INSERT INTO account_returning_reward_claims VALUES('return-205',2000000000,4,1,0,1,100000,2,123456)");
                    using var tx = db.BeginTransaction();
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_rename_pending", "CHECK(source_uid>=0)", "CHECK(source_uid>0)");
                    tx.Commit();
                }
                if (scenario == "partial205") Execute(db, "DROP TABLE account_returning_reward_claims");
                if (scenario == "unknown-rename205") Execute(db, "ALTER TABLE character_rename_pending ADD COLUMN unknown INTEGER");
                if (scenario == "late205") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                Execute(db, "PRAGMA user_version=205");
            }
            string before = Snapshot(path);
            if (scenario is not "upstream205" and not "local205")
            {
                RejectDatabase(() => new CharacterRepository(path));
                That(Snapshot(path) == before, "205兼容拒绝不完整或未知结构，末段失败回滚更名约束和版本：" + scenario);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                That(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:777" &&
                    Query(db, "SELECT source_uid FROM character_rename_pending") is 12345L,
                    "两种已发布205升级保留角色资产及更名资格：" + scenario);
                That((Query(db, "SELECT progress FROM account_returning_mission_progress") is 7L) == (scenario == "local205") &&
                    (Query(db, "SELECT item_count FROM account_returning_reward_claims") is 2L) == (scenario == "local205"),
                    "本地205保留任务及领取事实，上游205只建立空账本：" + scenario);
                Execute(db, "UPDATE character_rename_pending SET source_uid=0");
                That(Query(db, "SELECT source_uid FROM character_rename_pending") is 0L, "两种205升级后均支持商城更名资格来源零UID");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            That(Snapshot(path) == saved, "205兼容升级重复打开幂等：" + scenario);
        }
    }
}
