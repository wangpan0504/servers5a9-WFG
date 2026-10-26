using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunExperienceBonuses()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-exp-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root,"game.db");
        try
        {
            var database = new SqliteGameDatabase(path);
            using var owner = new AccountOwners().Acquire("exp-schema", () => { });
            var role = new CharacterRepository(path).Create(owner,new NewCharacter("ExpMigration",new(38,1,450,234),null));
            using (var db = database.Open())
            {
                Execute(db,$"""
                    INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
                      task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
                      total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,
                      after_experience,revision,growth_contract_experience)
                    VALUES({role.Id},'exp-schema','old-event','old-instance',3145,1,1,3,76126,zeroblob(16),x'01',1,
                      10,2,13,0,'old','old',1,1,0,13,1,1);
                    CREATE INDEX bonus_migration_index ON character_dungeon_settlement_receipts(account);
                    CREATE TRIGGER bonus_migration_trigger AFTER UPDATE ON character_dungeon_settlement_receipts BEGIN SELECT 1; END;
                    """);
                RemoveExperienceBonusSchema(db);
                Execute(db,"PRAGMA user_version=65");
            }
            string before = Data(path), old = Snapshot(path);
            using (var db = Open(path))
            {
                using (var tx = db.BeginTransaction())
                {
                    DungeonExperienceBonusSchema.Upgrade(db,tx);
                    tx.Rollback();
                }
            }
            Check(Snapshot(path) == old,"经验列、CHECK及版本在事务回滚后完整恢复");
            _ = new SqliteGameDatabase(path);
            Check(Data(path) == before && QueryOn(path,"PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                QueryOn(path,"SELECT other_bonus_experience FROM character_dungeon_settlement_receipts") is 0L &&
                QueryOn(path,"SELECT bonus_json FROM character_dungeon_settlement_receipts") is "[]" &&
                QueryOn(path,"SELECT proficiency_gain FROM character_dungeon_settlement_receipts") is 0L &&
                QueryOn(path,"SELECT count(*) FROM characters WHERE selected_skill_tree<>-1") is 0L &&
                QueryOn(path,"SELECT count(*) FROM sqlite_schema WHERE name IN ('bonus_migration_index','bonus_migration_trigger')") is 2L,
                "真实65旧库升级到当前版本保留全表原值、UID序列和历史回执，旧分项缺省0且无补发");
            string current = Snapshot(path);
            _ = new SqliteGameDatabase(path);
            Check(Snapshot(path) == current,"重复打开新结构不重复迁移、修改资产或重建表");
            using (var db = Open(path))
            {
                RemoveExperienceBonusSchema(db);
                Execute(db,"ALTER TABLE character_dungeon_settlement_receipts ADD COLUMN unexpected INTEGER;PRAGMA user_version=65");
            }
            string malformed = Snapshot(path);
            RejectDatabase(() => _ = new SqliteGameDatabase(path));
            Check(Snapshot(path) == malformed,"未知旧结构拒绝后原库、版本和数据不变");
        }
        finally { Directory.Delete(root,true); }

        static string Data(string file)
        {
            using var db = Open(file);
            return string.Join('|', TableNames(db).Select(table => table + ":" + Rows(db,table,
                ColumnNames(db,table).Where(column => column is not "\"other_bonus_experience\"" and not "\"bonus_json\"" and not "\"proficiency_gain\"" and not "\"selected_skill_tree\"").ToArray())));
        }
    }
}
