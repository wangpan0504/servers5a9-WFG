using Microsoft.Data.Sqlite;
using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // 只用于把当前空团本夹具还原为真实v108及更早结构；禁止丢弃业务事实。
    internal static void RewindRaidLifecycle(SqliteConnection db)
    {
        RemovePcRoomSchema(db);
        Execute(db, "DROP INDEX IF EXISTS character_raid_reward_epoch");
        if (Query(db, "SELECT count(*) FROM pragma_table_info('character_raid_reward_receipts') WHERE name='reward_epoch'") is 1L)
        {
            if (Query(db, "SELECT count(*) FROM character_raid_reward_receipts WHERE reward_epoch<>0") is not 0L)
                throw new InvalidOperationException("旧库夹具不能丢弃已刷新团本奖励世代");
            Execute(db, "ALTER TABLE character_raid_reward_receipts DROP COLUMN reward_epoch");
        }
        if (Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_raid_runs'") is 1L)
        {
            if (Query(db, "SELECT count(*) FROM character_raid_runs") is not 0L)
                throw new InvalidOperationException("旧库夹具不能丢弃已签发团本生命周期");
            Execute(db, "DROP TABLE character_raid_runs");
        }
        if (Query(db, "SELECT count(*) FROM pragma_table_info('character_raid_reward_receipts') WHERE name='raid_type'") is 1L)
        {
            if (Query(db, "SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type<>0") is not 0L)
                throw new InvalidOperationException("旧库夹具不能丢弃Luke奖励类型");
            Execute(db, "DROP INDEX IF EXISTS character_raid_rewards_type_week; ALTER TABLE character_raid_reward_receipts DROP COLUMN raid_type");
        }
    }

    internal static void RewindRaidLifecycle(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); RewindRaidLifecycle(db);
    }
    static void RaidLifecycleUpgrade(string root)
    {
        root = Path.Combine(root, "raid-lifecycle");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "column-conflict", "late-failure" })
        {
            string path = Path.Combine(root, "raid-109-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RewindRaidLifecycle(db);
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('raid-old',0,'RaidOld',7,38,1,450,234,0,123);
                    INSERT INTO character_raid_reward_receipts VALUES(1,'raid-old','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0,0,1,20261022,0,'{"legacy":true}','[]',1);
                    PRAGMA user_version=108;
                    """);
                if (scenario == "column-conflict") Execute(db, "ALTER TABLE character_raid_reward_receipts ADD COLUMN raid_type TEXT");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "109未知已有类型列或末段失败整笔回滚，不跳过未知结构");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT raid_type||':'||plan_json FROM character_raid_reward_receipts") is "0:{\"legacy\":true}",
                    "108旧Anton行默认type0且JSON原样保留");
                Check(Query(db, "SELECT count(*) FROM character_raid_runs") is 0L,
                    "109不补造旧入场或阶段资格");
                Check(Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "迁移不改角色资产");
            }
            // 145只扩展已证类型约束，保留原Luke行和完整两阶段/奖励事实。
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES(1,'raid-old','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',1,'fixture/luke',1,0)");
                using (var tx = db.BeginTransaction())
                {
                    SqliteMigrations.ReplaceTableConstraint(db, tx, "character_raid_runs", "CHECK(raid_type IN(0,1,2,3))", "CHECK(raid_type=1)");
                    tx.Commit();
                }
                Execute(db, "PRAGMA user_version=144");
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT source FROM character_raid_runs WHERE raid_type=1") is "fixture/luke", "145升级保留原Luke身份行");
                Execute(db, "INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES(1,'raid-old','cccccccccccccccccccccccccccccccc',0,'fixture/anton',1,0)");
                Check(Query(db, "SELECT count(*) FROM character_raid_runs") is 2L, "145同时支持Anton与Luke的隔离生命周期");
            }
            string upgraded = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == upgraded, "二次正常打开不重复迁移或改旧回执");
        }
    }

}
