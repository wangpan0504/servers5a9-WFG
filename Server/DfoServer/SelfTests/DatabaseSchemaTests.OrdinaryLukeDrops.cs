using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void OrdinaryLukeDropUpgrade(string root)
    {
        root = Path.Combine(root, "luke-drop-claims");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('luke-drop',0,'LukeDrop',7,38,1,450,234,0,123);
                    INSERT INTO character_luke_normal_progress SELECT id,account,20261026,3 FROM characters;
                    DROP TABLE character_luke_normal_drop_claims;
                    PRAGMA user_version=105;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE character_luke_normal_drop_claims(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "首击日资格迁移失败整体回滚版本、旧进度及资产");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_luke_normal_drop_claims") is 0L &&
                    Query(db, "SELECT progress FROM character_luke_normal_progress") is 3L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "105升级空资格表，不回填首击/掉落/旧进度");
                Execute(db, """
                    INSERT INTO character_luke_normal_drop_claims SELECT id,account,20261026,56063,
                      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' FROM characters;
                    """);
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db, "INSERT INTO character_luke_normal_drop_claims SELECT * FROM character_luke_normal_drop_claims"),
                    "同角色日源怪只有一条资格");
                foreach (string change in new[] { "source_monster_id=0", "day=0", "account='other'", "first_event_id='bad'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db, "UPDATE character_luke_normal_drop_claims SET " + change),
                        "日资格拒绝非法来源/日期/账号/事件");
                Execute(db, """
                    INSERT INTO character_luke_normal_drop_claims SELECT character_id,account,20261027,source_monster_id,
                      'cccccccccccccccccccccccccccccccc','dddddddddddddddddddddddddddddddd' FROM character_luke_normal_drop_claims;
                    """);
                Check(Query(db, "SELECT count(*) FROM character_luke_normal_drop_claims") is 2L,
                    "同源怪不同业务日独立保留，不覆盖旧Run资格");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开保留非空资格，无自动清理或补发");
            using (var db = Open(path)) Execute(db, "DROP TABLE character_luke_normal_drop_claims");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
