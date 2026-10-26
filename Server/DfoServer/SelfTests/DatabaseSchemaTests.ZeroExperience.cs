using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void ZeroExperienceUpgrade(string root)
    {
        string folder = Path.Combine(root, "zero-experience");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "game.db");
        var repository = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("zero-schema", () => { });
        var role = repository.Create(owner, new NewCharacter("ZeroSchema", new(38, 1, 450, 234), null));
        using (var db = Open(path))
        {
            Execute(db, $"""
                INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,
                  task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,
                  monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision)
                VALUES({role.Id},'zero-schema','old','instance',0,1,1,1,1,1,1,13,'old','old',1,3,1,1,0,13,1);
                CREATE INDEX zero_schema_index ON character_dungeon_growth_receipts(account);
                CREATE TRIGGER zero_schema_trigger AFTER UPDATE ON character_dungeon_growth_receipts BEGIN SELECT 1; END;
                """);
            RewindZeroExperience(db);
            Execute(db, "PRAGMA user_version=77");
        }
        string previous = Snapshot(path);
        using (var db = Open(path))
        {
            using var tx = db.BeginTransaction();
            ZeroExperienceDungeonSchema.Upgrade(db, tx);
            tx.Rollback();
        }
        Check(Snapshot(path) == previous, "零经验迁移回滚保留原表、数据和版本");
        _ = new SqliteGameDatabase(path);
        Check(QueryOn(path, "SELECT amount FROM character_dungeon_growth_receipts") is 13L &&
            QueryOn(path, "SELECT count(*) FROM sqlite_schema WHERE name IN ('zero_schema_index','zero_schema_trigger')") is 2L &&
            Convert.ToInt32(QueryOn(path, "PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
            "v77升级保留历史经验、索引和触发器，不补发或改写旧回执");
        using (var db = Open(path))
        {
            Execute(db, "UPDATE character_dungeon_growth_receipts SET amount=0,after_experience=before_experience");
            Reject<SqliteException>(() => Execute(db, "UPDATE character_dungeon_growth_receipts SET amount=-1"), "零经验下界仍拒绝负数");
            Check(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "迁移后完整性与外键保持");
        }
        string current = Snapshot(path);
        _ = new SqliteGameDatabase(path);
        Check(Snapshot(path) == current, "重开新库不改资产或重复迁移");
        using (var db = Open(path))
        {
            Execute(db, "UPDATE character_dungeon_growth_receipts SET amount=13,after_experience=13");
            RewindZeroExperience(db);
            Execute(db, "ALTER TABLE character_dungeon_settlement_receipts ADD COLUMN unexpected INTEGER;PRAGMA user_version=77");
        }
        string malformed = Snapshot(path);
        RejectDatabase(() => _ = new SqliteGameDatabase(path));
        Check(Snapshot(path) == malformed, "未知旧结构拒绝升级并保留原数据、约束与版本");
    }

    static void RewindZeroExperience(SqliteConnection db)
    {
        RemoveSkillTreeExpansionSchema(db);
        using var tx = db.BeginTransaction();
        using var query = db.CreateCommand(); query.Transaction = tx;
        foreach (var row in new[] { (Table: SettlementProficiencySchema.Growth, Current: ZeroExperienceDungeonSchema.NewGrowth, Previous: ZeroExperienceDungeonSchema.OldGrowth),
            (Table: SettlementProficiencySchema.Settlement, Current: ZeroExperienceDungeonSchema.NewTotal, Previous: ZeroExperienceDungeonSchema.OldTotal) })
        {
            query.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='" + row.Table + "'";
            if (query.ExecuteScalar() is 1L)
                SqliteMigrations.ReplaceTableConstraint(db, tx, row.Table, row.Current, row.Previous);
        }
        tx.Commit();
    }
}
