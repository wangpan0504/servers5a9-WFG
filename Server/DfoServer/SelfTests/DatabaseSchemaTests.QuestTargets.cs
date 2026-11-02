using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RewindQuestTargetTypes(string path)
    {
        using var db = Open(path); RewindQuestTargetTypes(db);
    }

    internal static void RewindQuestTargetTypes(SqliteConnection db)
    {
        const string table = "character_quest_completion_receipts";
        if (Query(db, "SELECT sql FROM sqlite_schema WHERE name='" + table + "'") is not string sql ||
            !SqliteMigrations.ReceiptShapeApplied(sql, table, SqliteMigrations.QuestCompletionSchemaV180)) return;
        Check(Query(db, "SELECT count(*) FROM " + table + " WHERE condition_type NOT IN(0,1,2,3,4,5,6,12,17,19,25,31,39,40,42,44,45,49,60,64,65,67,70)") is 0L,
            "旧库夹具不能删除新版目标的真实领奖");
        ChangeReceiptDefinition(db, table, _ => SqliteMigrations.QuestCompletionSchemaV127);
    }

    internal static void RunQuestTargetsCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-quest-target-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string scenario in new[] { "valid", "unknown", "late-failure" })
            {
                string path = Path.Combine(root, scenario + ".db");
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                    Execute(db, "INSERT INTO characters(account,slot,name,town,area,x,y,direction) VALUES('target-schema',0,'TargetSchema',1,0,10,10,0)");
                    RewindQuestTargetTypes(db);
                    Execute(db, """
                        INSERT INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,generation,rule_id,
                        resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
                        experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,
                        rewards,condition_type,plan_snapshot)
                        VALUES(1,'target-schema','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',100,1,'fixture','fixture','fixture',0,0,0,'1',0,
                        0,0,1,1,0,0,0,1,'[]',17,'{}');
                        CREATE INDEX target_schema_index ON character_quest_completion_receipts(quest_id);
                        CREATE TRIGGER target_schema_trigger AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT 1; END;
                        PRAGMA user_version=179;
                        """);
                    if (scenario == "unknown") ChangeReceiptDefinition(db, "character_quest_completion_receipts",
                        text => text.Replace("native_index>=0", "native_index>=0 AND native_index<999", StringComparison.Ordinal));
                    if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                }
                string before = Snapshot(path);
                if (scenario != "valid")
                {
                    RejectDatabase(() => new CharacterRepository(path));
                    Check(Snapshot(path) == before, "未知结构及末段失败完整回滚领奖表、历史记录、索引和库版本");
                    continue;
                }
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                    Check(Query(db, "SELECT condition_type FROM character_quest_completion_receipts") is 17L,
                        "历史17号本人分解回执原值保留，不能改写成新类型66");
                    Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name IN('target_schema_index','target_schema_trigger')") is 2L &&
                        Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "目标迁移保留附属对象和外键");
                    Execute(db, "UPDATE character_quest_completion_receipts SET condition_type=66");
                    foreach (int invalid in new[] { 50, 51, 61 })
                        DfoServer.Testing.TestCheck.Reject<SqliteException>(() => Execute(db, "UPDATE character_quest_completion_receipts SET condition_type=" + invalid),
                            "客户端没有类型赋值的空位不能写入领奖回执");
                }
                string upgraded = Snapshot(path); _ = new CharacterRepository(path);
                Check(Snapshot(path) == upgraded, "新目标迁移重开幂等，类型66无需重写回执内容");
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
