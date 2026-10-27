using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void OrdinaryLukeUpgrade(string root)
    {
        root = Path.Combine(root, "luke-progress");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "luke-progress-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RewindRaidLifecycle(db);
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('luke-old',0,'LukeOld',7,38,1,450,234,0,123);
                    DROP TABLE character_luke_normal_progress;
                    INSERT INTO character_boss_selection_buff SELECT id,account,'{"ItemId":490705256,"Remaining":3,"UntilMilliseconds":2000000000000,"Definition":{"BuffId":300,"DungeonId":7191,"Entries":5,"Weights":[{"MapId":92689,"Weight":40},{"MapId":92690,"Weight":40},{"MapId":92697,"Weight":20}]}}' FROM characters;
                    UPDATE characters SET npc_favor='[{"NpcId":17,"Points":1234}]';
                    INSERT INTO account_tag_evildom_clearances VALUES('legacy','luke-old',20261026,20261026,1,2);
                    PRAGMA user_version=104;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE character_luke_normal_progress(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "卢克进度迁移冲突/末段失败整体回滚DDL与版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_luke_normal_progress") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123", "104升级只建空进度不补历史、不改资产");
                Check(Query(db, "SELECT json_extract(state_json,'$.Remaining') FROM character_boss_selection_buff") is 3L &&
                    Query(db, "SELECT npc_favor FROM characters") is "[{\"NpcId\":17,\"Points\":1234}]" &&
                    Query(db, "SELECT count(*) FROM account_tag_evildom_clearances") is 1L,
                    "105升级保留既有药效、NPC好感与TAG历史，不串改上游迁移状态");
                Execute(db, "INSERT INTO character_luke_normal_progress SELECT id,account,20261026,3 FROM characters");
                foreach (string change in new[] { "progress=-1", "progress=7", "progress=1.5", "account='other'" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db, "UPDATE character_luke_normal_progress SET " + change),
                        "进度范围及角色账号归属约束");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开保留非空进度，不重做迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE character_luke_normal_progress");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
