using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunPvpMissionCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pvp-mission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { PvpMissionUpgrade(root); }
        finally { Directory.Delete(root, true); }
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
                Execute(db, "DROP TABLE character_pvp_mission_rewards; DROP TABLE character_pvp_mission_facts; PRAGMA user_version=146");
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
