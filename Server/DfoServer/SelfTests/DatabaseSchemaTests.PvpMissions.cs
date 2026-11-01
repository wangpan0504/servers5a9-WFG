using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunPvpMissionCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pvp-mission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { PvpMissionUpgrade(root); PvpDailyUpgrade(root); }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void PvpDailyUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-daily-schema");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade", "precreated", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "daily-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                if (scenario == "precreated") Execute(db, "PRAGMA user_version=147; CREATE TRIGGER daily_preserve BEFORE INSERT ON character_pvp_daily_missions BEGIN SELECT RAISE(ABORT,'preserved'); END");
                else Execute(db, "DROP TABLE character_pvp_daily_missions; PRAGMA user_version=147");
                if (scenario == "conflict") Execute(db, "CREATE TABLE character_pvp_daily_missions(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario is not ("upgrade" or "precreated"))
            {
                string previous = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == previous, "每日迁移冲突或最终校验失败时DDL与版本共同回滚");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path)) Check(Query(db, "SELECT count(*) FROM character_pvp_daily_missions") is 0L &&
                Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion,
                "147旧库升级只建空每日账本，不补造历史分配及奖励");
            if (scenario == "precreated") using (var db = Open(path))
                Check(Query(db, "SELECT count(*) FROM sqlite_schema WHERE name='daily_preserve'") is 1L,
                    "已存在的完全匹配结构只复验，不重建或删除现有触发器");
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(saved == Snapshot(path), "每日迁移重开幂等");
        }
    }

    static void PvpMissionUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-missions");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "fact-conflict", "claim-conflict", "late-failure" })
        {
            string path = Path.Combine(root, "mission-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "DROP TABLE character_pvp_daily_missions; DROP TABLE character_pvp_mission_rewards; DROP TABLE character_pvp_mission_facts; PRAGMA user_version=146");
                if (scenario == "fact-conflict") Execute(db, "CREATE TABLE character_pvp_mission_facts(unknown INTEGER)");
                if (scenario == "claim-conflict") Execute(db, "CREATE TABLE character_pvp_mission_rewards(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string previous = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == previous, "决斗训练迁移冲突及后续结构校验失败完整回滚DDL和版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM character_pvp_mission_facts") is 0L &&
                    Query(db, "SELECT count(*) FROM character_pvp_mission_rewards") is 0L,
                    "旧库升级只建空事实和奖励账本，不补造玩家完成位");
            string snapshot = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == snapshot, "决斗任务库重开不重复迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE character_pvp_mission_rewards");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
