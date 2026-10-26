using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunPermanentStats()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-permanent-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("permanent-schema", () => { });
            var role = repository.Create(owner, new NewCharacter("PermOld", new(38, 1, 450, 234), null));
            using (var db = Open(path))
            {
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "DROP TABLE character_permanent_stats; PRAGMA user_version=67");
            }
            string previous = Snapshot(path);
            using (var db = Open(path))
            {
                using var tx = db.BeginTransaction();
                SqliteMigrations.Execute(db, tx, PermanentStatSchema.Sql);
                SqliteMigrations.Execute(db, tx, "PRAGMA user_version=68");
                tx.Rollback();
            }
            Check(Snapshot(path) == previous, "永久属性表和版本在迁移失败时一起回滚");
            repository = new(path);
            Check(repository.LoadCharacter(owner, role.Slot).PermanentStats == PermanentStatBonuses.Empty &&
                QueryOn(path, "SELECT count(*) FROM character_permanent_stats") is 0L &&
                Convert.ToInt32(QueryOn(path, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
                "真实v67旧库增量升级，不推测历史属性来源、不改原角色revision或资产");
            using (var db = Open(path))
            {
                Execute(db, $"INSERT INTO character_permanent_stats VALUES({role.Id},'permanent-schema',250,250,50,50,50,50,10,10)");
                foreach (string field in new[] { "max_hp", "max_mp", "strength", "vitality", "intelligence", "spirit", "move_speed", "all_element_resistance" })
                    Reject<SqliteException>(() => Execute(db, $"UPDATE character_permanent_stats SET {field}=-1"), "永久来源不接受负值 " + field);
                Reject<SqliteException>(() => Execute(db, "UPDATE character_permanent_stats SET strength=32768"), "属性来源符合短字段上限");
                Reject<SqliteException>(() => Execute(db, "UPDATE character_permanent_stats SET max_hp=4294967296"), "HP来源符合DWORD上限");
                Reject<SqliteException>(() => Execute(db, "UPDATE character_permanent_stats SET account='another'"), "来源不能转移到其他账号");
            }
            var after = new CharacterRepository(path).LoadCharacter(owner, role.Slot);
            Check(after.PermanentStats == new PermanentStatBonuses(250, 250, 50, 50, 50, 50, 10, 10) &&
                after.Revision == role.Revision && after.Learning.Experience == role.Learning.Experience,
                "新仓储恢复额外来源，不把它存成或覆盖派生总额");
            string complete = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == complete, "重开当前库不重复授予或重写永久来源");
            repository.Delete(owner, repository.LoadDeletionTarget(owner, role.Slot));
            Check(QueryOn(path, "SELECT count(*) FROM character_permanent_stats") is 0L, "删角清理自身属性来源，不留悬空外键");
            var legacy = repository.Create(owner, new NewCharacter("OldSkin", new(38, 1, 450, 234), null));
            using (var db = Open(path))
            {
                Execute(db, $"""
                    INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
                      task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
                      total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,
                      after_experience,revision,growth_contract_experience)
                    VALUES({legacy.Id},'permanent-schema','old-event','old-instance',3145,1,1,3,76126,zeroblob(16),x'01',1,
                      10,2,13,0,'old','old',1,1,0,13,1,1);
                    """);
                RemoveExperienceBonusSchema(db);
                Execute(db, "DROP TABLE character_permanent_stats; PRAGMA user_version=66");
            }
            var migrated = new CharacterRepository(path).LoadCharacter(owner, legacy.Slot);
            string skin = (string)QueryOn(path, "SELECT weapon_skin_state FROM characters WHERE name='OldSkin'")!;
            Check(QuestCompletionRepositoryTests.Same(migrated, legacy) &&
                QueryOn(path, "SELECT other_bonus_experience FROM character_dungeon_settlement_receipts WHERE event_id='old-event'") is 0L &&
                QueryOn(path, "SELECT total_experience FROM character_dungeon_settlement_receipts WHERE event_id='old-event'") is 13L &&
                skin.Contains("\"Owned\":[]", StringComparison.Ordinal),
                "已确认旧皮肤v66补齐主线经验列后升级当前版本，保留角色、皮肤及冻结奖励，不回放历史");
        }
        finally { Directory.Delete(root, true); }
    }
}
