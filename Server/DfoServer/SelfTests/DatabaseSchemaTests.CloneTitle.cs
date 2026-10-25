using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void CloneTitleUpgrade(string root)
    {
        root = Path.Combine(root, "clone-title");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "clone-title-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('clone-title',0,'CloneOld',7,38,1,450,234,0,123)");
                Check(Query(db, "SELECT clone_title_item_id FROM characters") is 0L, "新库称号动画缺省0");
                Execute(db, "ALTER TABLE characters DROP COLUMN clone_title_item_id; PRAGMA user_version=98");
                if (scenario == "conflict")
                    Execute(db, "ALTER TABLE characters ADD COLUMN clone_title_item_id TEXT NOT NULL DEFAULT '0'");
                if (scenario == "late-failure")
                    Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "动画迁移拒绝未知列或末段校验失败，DDL、原角色与版本整笔回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT clone_title_item_id||':'||revision||':'||gold FROM characters") is "0:7:123",
                    "真实v98升级仅新增零动画，不改角色revision、金币或存量展示");
                foreach (string value in new[] { "-1", "4294967296", "1.5", "NULL", "'invalid'" })
                    Reject<SqliteException>(() => Execute(db, "UPDATE characters SET clone_title_item_id=" + value),
                        "称号动画列拒绝负数、越界、非整数及空值");
                Execute(db, "UPDATE characters SET clone_title_item_id=4294967295");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开数据库保留完整u32动画，不重置或重复迁移");
        }
    }
}
