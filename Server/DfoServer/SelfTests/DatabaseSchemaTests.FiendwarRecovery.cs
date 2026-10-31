using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void FiendwarRecoveryUpgrade(string root)
    {
        root = Path.Combine(root, "fiendwar-recovery"); Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "wrong-table", "name-collision" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    DROP TABLE character_fiendwar_recovery;
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('old-fw-recovery',0,'OldFwRecovery',7,38,1,450,234,0,123);
                    INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                    VALUES(1,'old-fw-recovery','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',2,'fixture/old-recovery',1,0);
                    PRAGMA user_version=168;
                    """);
                if (scenario == "wrong-table") Execute(db, "CREATE TABLE character_fiendwar_recovery(character_id TEXT)");
                if (scenario == "name-collision") Execute(db, "CREATE VIEW character_fiendwar_recovery AS SELECT id FROM characters");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "169结构不兼容时回滚，不抬版本、不丢原资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM character_fiendwar_recovery") is 0L &&
                    Query(db, "SELECT state||':'||source FROM character_raid_runs") is "0:fixture/old-recovery",
                    "169迁移和仓储构造不补造旧原票，也不自行终结活跃团");
                Check(Query(db, "SELECT revision||':'||gold FROM characters") is "7:123" &&
                    Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                    "169升级保留角色资产并连续更新版本");
            }
            var repository = new CharacterRepository(path);
            Check(repository.RecoverInterruptedFiendwar(DateTimeOffset.UtcNow) == new Game.Raids.FiendwarInterruptionResult(0, 0, 1),
                "旧run仅在宿主显式恢复时按无原票收尾，不补发未知门票");
        }
    }
}
