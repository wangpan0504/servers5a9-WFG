using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void DungeonTicketQuotaUpgrade(string root)
    {
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "upgrade-v201", "upgrade-v202", "conflict", "late-failure", "newer" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                Execute(db, "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('ticket-old',0,'TicketOld',7,38,1,450,234,0,123)");
                Execute(db, """
                    DROP TABLE account_dungeon_incount_extra;
                    DROP TABLE character_dungeon_reward_extra;
                    DROP TABLE account_single_dungeon_bindings;
                    PRAGMA user_version=202;
                    """);
                if (scenario == "upgrade-v201")
                {
                    RemoveClearRewardResetSchema(db);
                    Execute(db, "PRAGMA user_version=201");
                }
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_dungeon_incount_extra(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "newer") Execute(db, $"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            }
            string before = Snapshot(path);
            if (!scenario.StartsWith("upgrade-", StringComparison.Ordinal))
            {
                RejectDatabase(() => new CharacterRepository(path));
                That(Snapshot(path) == before, "追加券迁移冲突、末段失败及较新版本拒绝保留原库：" + scenario);
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                That(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT (SELECT count(*) FROM account_dungeon_incount_extra)+(SELECT count(*) FROM character_dungeon_reward_extra)+(SELECT count(*) FROM account_single_dungeon_bindings)") is 0L &&
                    Query(db, "SELECT count(*) FROM character_clear_reward_resets") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "v201连续升级及v202单步升级只追加空账本，保留原角色资产与版本：" + scenario);
            string saved = Snapshot(path);
            _ = new CharacterRepository(path);
            That(Snapshot(path) == saved, "追加券迁移重开幂等，不补造旧额度或绑定");
        }
    }
}
