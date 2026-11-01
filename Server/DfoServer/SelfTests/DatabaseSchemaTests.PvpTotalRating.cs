using DfoServer.Persistence;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    internal static void RunPvpTotalRatingCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-total-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { PvpTotalRatingUpgrade(root); }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void PvpTotalRatingUpgrade(string root)
    {
        root = Path.Combine(root, "pvp-total-rating");
        Directory.CreateDirectory(root);
        foreach (string scenario in new[] { "ordinary", "conflict", "late-failure", "index-conflict" })
        {
            string path = Path.Combine(root, scenario + ".db");
            _ = new CharacterRepository(path);
            using (var db = Open(path))
            {
                // 回退点 137 高于团本生命周期 109，该步不会重跑，团本表须保持当前结构。
                Execute(db, """
                    DROP TABLE account_pvp_total_rating_claims;
                    DROP TABLE account_pvp_total_ratings;
                    PRAGMA user_version=137;
                    """);
                if (scenario == "conflict") Execute(db, "CREATE TABLE account_pvp_total_ratings(unknown INTEGER)");
                if (scenario == "late-failure") Execute(db, "ALTER TABLE accounts DROP COLUMN disabled");
                if (scenario == "index-conflict") Execute(db, "CREATE INDEX ix_pvp_total_rating_pending ON accounts(account_id)");
            }
            if (scenario != "ordinary")
            {
                string previous = Snapshot(path);
                RejectDatabase(() => new CharacterRepository(path));
                Check(Snapshot(path) == previous, "总力评分迁移冲突或末段失败完整回滚DDL及版本");
                continue;
            }
            _ = new CharacterRepository(path);
            using (var db = Open(path))
                Check(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                    Query(db, "SELECT count(*) FROM account_pvp_total_ratings") is 0L &&
                    Query(db, "SELECT count(*) FROM account_pvp_total_rating_claims") is 0L,
                    "137旧库升级仅创建空总力账本，不补造奖励来源");
            string current = Snapshot(path);
            _ = new CharacterRepository(path);
            Check(Snapshot(path) == current, "总力评分库重开不重复迁移");
            using (var db = Open(path)) Execute(db, "DROP INDEX ix_pvp_total_rating_pending");
            RejectDatabase(() => new CharacterRepository(path));
        }
    }
}
