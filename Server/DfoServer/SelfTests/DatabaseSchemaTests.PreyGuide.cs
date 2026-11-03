using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void PreyGuideUpgrade(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "late-failure", "newer" })
        {
            string path = Path.Combine(root, "prey-schema-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold) VALUES(1,'prey-old',0,'PreyOld',7,38,1,450,234,0,123)");
                // 夹具明确还原162约束，不调用生产迁移的“保留已知后续结构”兼容分支。
                Execute(db, """
                    PRAGMA writable_schema=ON;
                    UPDATE sqlite_schema SET sql=replace(sql,'CHECK(raid_type IN(0,1,2,3))','CHECK(raid_type IN(0,1))')
                      WHERE name IN('character_raid_runs','character_raid_reward_receipts');
                    UPDATE sqlite_schema SET sql=replace(sql,
                      'state=2 AND clear_week IS NOT NULL AND clear_week>0 AND ((raid_type=2 AND phase0_event IS NOT NULL AND phase1_event IS NULL) OR (raid_type<>2 AND phase1_event IS NOT NULL))',
                      'state=2 AND clear_week IS NOT NULL AND clear_week>0 AND phase1_event IS NOT NULL') WHERE name='character_raid_runs';
                    PRAGMA writable_schema=OFF; PRAGMA schema_version=9001;
                    """);
                Execute(db, "DROP INDEX character_raid_account_admission; PRAGMA user_version=162");
                foreach (int type in new[] { 0, 1 })
                {
                    string instance = new string((char)('a' + type), 32);
                    Execute(db, $"INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES(1,'prey-old','{instance}',{type},'old',1,0)");
                    Execute(db, $"INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type) VALUES(1,'prey-old','{instance}',0,0,0,20261029,0,'{{}}','[]',1,{type})");
                }
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            string before = Snapshot(path);
            if (scenario != "upgrade")
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "普雷迁移末段失败及较新库拒绝保持原版本、结构和资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_raid_runs WHERE raid_type IN(0,1) AND state=0") is 2L &&
                    Query(db, "SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type IN(0,1)") is 2L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "162升级保留安徒恩、卢克历史收据和资产");
                Execute(db, "INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES(1,'prey-old','cccccccccccccccccccccccccccccccc',3,'prey',2,1)");
                Execute(db, "INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type) VALUES(1,'prey-old','cccccccccccccccccccccccccccccccc',0,0,0,20261029,0,'{}','[]',2,3)");
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                    "升级后普雷类型可写且外键和完整性保持");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "普雷升级重开不重复迁移或改变历史");
        }
    }
}
