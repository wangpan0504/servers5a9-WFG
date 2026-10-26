using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RemoveSkillTreeExpansionSchema(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM pragma_table_info('characters') WHERE name='selected_skill_tree'") is 1L)
        {
            if (Query(db, "SELECT count(*) FROM characters WHERE selected_skill_tree<>-1") is not 0L)
                throw new InvalidOperationException("旧库夹具不能丢弃已开通技能方案");
            Execute(db, "ALTER TABLE characters DROP COLUMN selected_skill_tree");
        }
    }

    static void SkillTreeUpgrade(string root)
    {
        root = Path.Combine(root, "skill-tree");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "skill-tree-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('tree-schema',0,'TreeOld',7,38,1,450,234,0,123)");
                Check(Query(db, "SELECT selected_skill_tree FROM characters") is -1L, "新角色缺省未开通技能方案");
                Execute(db, "INSERT INTO character_skill_points VALUES(1,0,205,0),(1,1,13000,46)");
                Execute(db, "INSERT INTO character_skill_bindings VALUES(1,0,0,5,1),(1,1,7,5,2)");
                RemoveSkillTreeExpansionSchema(db);
                Execute(db, "PRAGMA user_version=99");
                if (scenario == "conflict") Execute(db, "ALTER TABLE characters ADD COLUMN selected_skill_tree TEXT NOT NULL DEFAULT '-1'");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "技能方案迁移冲突或最终校验失败整笔回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT selected_skill_tree||':'||revision||':'||gold FROM characters") is "-1:7:123" &&
                    Query(db, "SELECT group_concat(sp||':'||tp) FROM character_skill_points ORDER BY context") is "205:0,13000:46" &&
                    Query(db, "SELECT group_concat(slot||':'||level) FROM character_skill_bindings ORDER BY context") is "0:1,7:2",
                    "真实v99升级保留金币、版本及两树全部绑定余额，不自动开通");
                foreach (string value in new[] { "-2", "2", "1.5", "NULL", "'invalid'" })
                    Reject<SqliteException>(() => Execute(db, "UPDATE characters SET selected_skill_tree=" + value), "方案列拒绝非法取值");
                Execute(db, "UPDATE characters SET selected_skill_tree=1");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开库不重置当前第二方案");
        }
    }
}
