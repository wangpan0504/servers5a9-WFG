using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void TagEvildomUpgrade(string root)
    {
        root = Path.Combine(root, "tag-evildom");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "tag-evildom-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('tag-old',0,'TagOld',7,38,1,450,234,0,123);
                    DROP TABLE account_tag_evildom_runs;
                    ALTER TABLE characters DROP COLUMN npc_favor;
                    DROP TABLE account_tag_evildom_configuration;
                    DROP TABLE account_tag_evildom_clearances;
                    PRAGMA user_version=100;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_tag_evildom_configuration(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "再战编队迁移冲突或末段失败时DDL、角色和版本整笔回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM account_tag_evildom_configuration") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "真实v100升级只建空配置表，不臆测旧队伍或改动资产");
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "当前版本重开不重做编队迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE account_tag_evildom_configuration");
            RejectDatabase(() => new CharacterRepository(path));
        }
        TagEvildomRunUpgrade(root);
    }

    static void TagEvildomRunUpgrade(string root)
    {
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "tag-run-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, """
                    DROP TABLE account_tag_evildom_runs;
                    INSERT INTO account_tag_evildom_clearances VALUES('legacy','tag-old',20261026,20261026,1,2);
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,npc_favor)
                    VALUES('tag-old',0,'TagHistory',7,38,1,450,234,0,'[{"NpcId":17,"Points":1234}]');
                    PRAGMA user_version=102;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_tag_evildom_runs(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "103准入回执迁移冲突/末段失败不改旧通关额度、NPC好感与版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "SELECT count(*) FROM account_tag_evildom_runs") is 0L &&
                    Query(db, "SELECT count(*) FROM account_tag_evildom_clearances") is 1L &&
                    Query(db, "SELECT revision FROM characters WHERE account='tag-old'") is 7L &&
                    Query(db, "SELECT npc_favor FROM characters WHERE account='tag-old'") is "[{\"NpcId\":17,\"Points\":1234}]",
                    "102升级保留非空NPC好感与旧通关，只建空准入/领奖回执不补邮件");
                Execute(db, """
                    INSERT INTO account_tag_evildom_runs(account,instance_id,first,second,task_attempt,configuration_revision,team_index,dungeon_id,resource_key,admitted_at)
                    VALUES('tag-old','11111111111111111111111111111111',1,2,1,1,0,9608,
                      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1);
                    """);
                foreach (string partial in new[] { "mail_id=1", "clear_event='11111111111111111111111111111111'", "reward_json='{}'", "elapsed_ms=0", "cleared_at=1" })
                    Reject<Microsoft.Data.Sqlite.SqliteException>(() => Execute(db, "UPDATE account_tag_evildom_runs SET " + partial),
                        "SQL不能借NULL三值逻辑留下半个已完成状态");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "103重开保留admitted、NPC好感与历史clearance，不自动恢复假通关");
        }
    }
}
