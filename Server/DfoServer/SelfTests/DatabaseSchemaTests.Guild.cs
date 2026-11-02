using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void GuildExtensionsUpgrade(string root)
    {
        root = Path.Combine(root, "guild-upgrade");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "v100", "v129", "v130", "collision", "channel-collision", "late-failure" })
        {
            string path = Path.Combine(root, "guild-upgrade-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                RemovePcRoomSchema(db);
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold,selected_skill_tree)
                    VALUES('guild-upgrade',0,'GuildOld',7,38,1,450,234,0,123,1);
                    INSERT INTO character_skill_points VALUES(1,0,205,0),(1,1,13000,46);
                    INSERT INTO guilds(guild_id,name,address,master_character_id,level,tier,experience,
                        public_funds,guild_crystal,created_at_unix)
                    VALUES(1,'Old guild','old-guild',1,3,1,1234,56789,12,1000);
                    INSERT INTO guild_members(guild_id,character_id,grade,contribution,joined_at_unix,last_login_unix)
                    VALUES(1,1,0,56,1000,1100);
                    """);
                Execute(db, "DROP TABLE guild_recommended_channels");
                if (scenario is "v130" or "channel-collision")
                    Execute(db, "PRAGMA user_version=130");
                else if (scenario == "v129")
                    Execute(db, """
                        DROP TABLE guild_npc_craft_receipts;
                        DROP TABLE guild_npc_craft_completions;
                        DROP TABLE guild_npc_crafts;
                        PRAGMA user_version=129;
                        """);
                else
                {
                    // 回退到 100 会让团本生命周期 109 重跑，先把团本表退回该步之前的形状；
                    // v129/v130/collision 分支的版本都高于 109，不能动团本表。
                    RewindRaidLifecycle(db);
                    RemoveGuildExtensionSchema(db);
                    Execute(db, "PRAGMA user_version=100");
                }
                if (scenario == "collision")
                    Execute(db, "CREATE VIEW guild_npc_crafts AS SELECT 1 AS guild_id");
                if (scenario == "channel-collision")
                    Execute(db, "CREATE VIEW guild_recommended_channels AS SELECT 1 AS guild_id");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario is "collision" or "channel-collision" or "late-failure")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "公会迁移撞名或末端校验失败时全部结构、版本及原有资产回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT gold||':'||revision||':'||selected_skill_tree FROM characters") is "123:7:1" &&
                    Query(db, "SELECT group_concat(sp||':'||tp) FROM character_skill_points ORDER BY context") is "205:0,13000:46" &&
                    Query(db, "SELECT level||':'||tier||':'||experience||':'||public_funds||':'||guild_crystal FROM guilds") is "3:1:1234:56789:12" &&
                    Query(db, "SELECT grade||':'||contribution||':'||joined_at_unix||':'||last_login_unix FROM guild_members") is "0:56:1000:1100",
                    "真实v100、v129及v130公会升级保留金币、双技能树、公会成长及原始会籍，不补历史奖励");
                Check(Query(db, "SELECT count(*) FROM guild_npc_crafts") is 0L &&
                    Query(db, "SELECT count(*) FROM guild_recommended_channels") is 0L &&
                    Query(db, "SELECT count(*) FROM guild_npc_craft_receipts") is 0L &&
                    Query(db, "SELECT count(*) FROM guild_npc_craft_completions") is 0L &&
                    Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L &&
                    Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='index' AND name='ix_guild_contribution_daily_cap'") is 1L,
                    "公会升级建立空制造账本和日额度索引，外键保持完整");
            }
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "公会新结构重开幂等，不重置成长与制造账本");
        }
    }
}
