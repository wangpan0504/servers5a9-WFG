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
    }
}
