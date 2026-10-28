using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    static void PvpTotalTeamUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-total-team");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure" })
        {
            string path = Path.Combine(root, "pvp-total-team-" + scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                // 回退点 133 高于团本生命周期 109，该步不会重跑，团本表须保持当前结构。
                Execute(db, """
                    INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold)
                    VALUES('team-old',0,'TeamOld',7,38,1,450,234,0,123);
                    DROP TABLE account_pvp_total_teams;
                    PRAGMA user_version=133;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_pvp_total_teams(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
            }
            if (scenario != "ordinary")
            {
                string before = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == before, "总力编队迁移冲突或末段失败回滚DDL、版本和角色数据");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM account_pvp_total_teams") is 0L &&
                    Query(db, "SELECT revision||':'||gold FROM characters") is "7:123",
                    "总力迁移只建空队伍表，不把既有角色自动登记或改写资产");
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "重开总力队伍库不重复迁移");
            using (var db = Open(path)) Execute(db, "DROP TABLE account_pvp_total_teams");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
