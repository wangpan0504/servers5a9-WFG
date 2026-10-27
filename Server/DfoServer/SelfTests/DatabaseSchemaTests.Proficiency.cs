using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // v74 只追加两张回执的熟练度到账列；回退到 v73 的夹具必须真的移除该列，升级后旧行按 0 补齐且不重算历史。
    internal static void RunSettlementProficiency()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-proficiency-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var database = new SqliteGameDatabase(path);
            using var owner = new AccountOwners().Acquire("prof-schema", () => { });
            var role = new CharacterRepository(path).Create(owner, new NewCharacter("ProfMigrate", new(38, 1, 450, 234), null));
            using (var db = database.Open())
            {
                Execute(db, $"""
                    INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
                      task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
                      total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,
                      after_experience,revision,growth_contract_experience)
                    VALUES({role.Id},'prof-schema','old-settlement','old-settlement-instance',3145,1,1,3,76126,zeroblob(16),x'01',1,
                      10,2,13,0,'old','old',1,1,0,13,1,1);
                    INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,
                      task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,
                      monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision)
                    VALUES({role.Id},'prof-schema','old-growth','old-growth-instance',3145,1,1,76126,20,1,1,13,'old','old',
                      1,3,1,1,0,13,1);
                    CREATE INDEX proficiency_migration_index ON character_dungeon_growth_receipts(account);
                    """);
                RemoveProficiencySchema(db);
                RewindRaidLifecycle(db);
                Execute(db, "PRAGMA user_version=73");
            }
            string old = Snapshot(path);
            using (var db = Open(path))
            {
                using var tx = db.BeginTransaction();
                SettlementProficiencySchema.Upgrade(db, tx);
                tx.Rollback();
            }
            Check(Snapshot(path) == old, "熟练度到账列及版本在事务回滚后完整恢复");
            _ = new SqliteGameDatabase(path);
            Check(QueryOn(path, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                QueryOn(path, "SELECT proficiency_gain FROM character_dungeon_settlement_receipts") is 0L &&
                QueryOn(path, "SELECT proficiency_gain FROM character_dungeon_growth_receipts") is 0L &&
                QueryOn(path, "SELECT amount FROM character_dungeon_growth_receipts") is 13L &&
                QueryOn(path, "SELECT before_experience FROM character_dungeon_settlement_receipts") is 0L &&
                QueryOn(path, "SELECT count(*) FROM sqlite_schema WHERE name='proficiency_migration_index'") is 1L,
                "真实73旧库升级到当前版本保留两张回执原值，熟练度到账缺省0且不补发历史");
            string current = Snapshot(path);
            _ = new SqliteGameDatabase(path);
            Check(Snapshot(path) == current, "重复打开新结构不重复迁移、修改资产或重建表");
            using (var db = Open(path))
            {
                RemoveProficiencySchema(db);
                RewindRaidLifecycle(db);
                Execute(db, "ALTER TABLE character_dungeon_settlement_receipts ADD COLUMN proficiency_gain TEXT;PRAGMA user_version=73");
            }
            string malformed = Snapshot(path);
            RejectDatabase(() => _ = new SqliteGameDatabase(path));
            Check(Snapshot(path) == malformed, "不同约束的熟练度列拒绝后原库、版本和数据不变");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS 熟练度到账列迁移、回滚与旧行缺省断言");

        static void RemoveProficiencySchema(SqliteConnection db)
        {
            RewindZeroExperience(db);
            if (Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_settlement_receipts') WHERE name='proficiency_gain'") is 1L)
                Execute(db, "ALTER TABLE character_dungeon_settlement_receipts DROP COLUMN proficiency_gain");
            if (Query(db, "SELECT count(*) FROM pragma_table_info('character_dungeon_growth_receipts') WHERE name='proficiency_gain'") is 1L)
                Execute(db, "ALTER TABLE character_dungeon_growth_receipts DROP COLUMN proficiency_gain");
        }
    }
}
