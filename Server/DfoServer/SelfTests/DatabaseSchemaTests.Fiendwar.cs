using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void FiendwarUpgrade(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "existing", "collision", "late-failure", "newer" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(id,account,slot,name,revision,town,area,x,y,direction,gold) VALUES(1,'fiend-old',0,'FiendOld',7,38,1,450,234,0,123)");
                if (scenario is not ("existing" or "collision")) Execute(db, "DROP TABLE account_fiendwar_guides");
                Execute(db, """
                    PRAGMA writable_schema=ON;
                    UPDATE sqlite_schema SET sql=replace(sql,'CHECK(raid_type IN(0,1,2,3))','CHECK(raid_type IN(0,1,3))')
                      WHERE name IN('character_raid_runs','character_raid_reward_receipts');
                    UPDATE sqlite_schema SET sql=replace(sql,
                      'state=2 AND clear_week IS NOT NULL AND clear_week>0 AND ((raid_type=2 AND phase0_event IS NOT NULL AND phase1_event IS NULL) OR (raid_type<>2 AND phase1_event IS NOT NULL))',
                      'state=2 AND clear_week IS NOT NULL AND clear_week>0 AND phase1_event IS NOT NULL') WHERE name='character_raid_runs';
                    PRAGMA writable_schema=OFF; PRAGMA schema_version=9001; PRAGMA user_version=163;
                    """);
                foreach (int type in new[] { 0, 1, 3 })
                {
                    string instance = new string((char)('a' + type), 32);
                    Execute(db, $"INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES(1,'fiend-old','{instance}',{type},'old',1,0)");
                    Execute(db, $"INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type) VALUES(1,'fiend-old','{instance}',0,0,0,20261029,0,'{{}}','[]',1,{type})");
                }
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "collision") Execute(db, "ALTER TABLE account_fiendwar_guides ADD COLUMN unknown INTEGER");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            string before = Snapshot(path);
            if (scenario is not ("upgrade" or "existing"))
            {
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "漩涡迁移失败或较新版本拒绝不改变原库");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM character_raid_runs WHERE raid_type IN(0,1,3)") is 3L &&
                    Query(db, "SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type IN(0,1,3)") is 3L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "漩涡迁移保留旧三类团本和资产");
                Execute(db, """
                    INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,
                      phase0_event,phase0_anchor,phase0_receipt,clear_week)
                    VALUES(1,'fiend-old','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',2,'fiend',2,2,'ffffffffffffffffffffffffffffffff',3,'{}',20261029);
                    INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at,raid_type) VALUES(1,'fiend-old','eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',0,0,0,20261029,1,'{}','[]',3,2);
                    """);
                Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L &&
                    Query(db, "SELECT state FROM character_raid_runs WHERE raid_type=2") is 2L, "漩涡单阶段通关与最终领奖收据可持久化");
                Check(Query(db, "SELECT count(*) FROM account_fiendwar_guides") is 0L,
                    "旧库升级建立独立引导绑定表，不凭历史团队通关占用名额");
            }
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == saved, "重开漩涡升级库不重复迁移或改变记录");
        }
    }
}
