using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static partial class DatabaseSchemaTests
{
    // 旧结构夹具不应带入未来刷新表；真实刷新事实不能作为空结构回退而被丢弃。
    internal static void RemoveClearRewardResetSchema(SqliteConnection db)
    {
        if (Query(db, "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name='character_clear_reward_resets'") is not 1L) return;
        if (Query(db, "SELECT count(*) FROM character_clear_reward_resets") is not 0L)
            throw new InvalidOperationException("旧库夹具不能丢弃真实奖励刷新事实");
        Execute(db, "DROP TABLE character_clear_reward_resets");
    }

    static void ClearRewardResetUpgrade(string root)
    {
        foreach (string scenario in new[] { "upgrade-v199", "upgrade-v200", "upgrade", "conflict", "late-failure", "current-missing", "current-weak" })
        {
            using var f = RaidQuotaEpochTests.CreateFixture();
            WeeklyEntryResetTests.SeedClearReward(f, f.Other.Id, "other", 291100320, 31, DateTimeOffset.UtcNow, claimed: true);
            // 已发布v200魔兽及v201收藏迁移不重排；分别验证跨版本连续升级和v201单步升级。
            f.Execute("DROP TABLE character_clear_reward_resets");
            int previousVersion = scenario == "upgrade-v199" ? 199 : scenario == "upgrade-v200" ? 200 : 201;
            if (previousVersion < 201)
            {
                Check(f.Query("SELECT count(*) FROM character_npc_bookmarks") is 0L, "旧库夹具不丢弃真实人物收藏");
                f.Execute("DROP TABLE character_npc_bookmarks");
            }
            else
                f.Execute($"INSERT INTO character_npc_bookmarks VALUES({f.Other.Id},'other',X'11000000')");
            if (previousVersion == 199)
            {
                Check(f.Query("SELECT count(*) FROM character_revised_beast_stage_rewards") is 0L &&
                    f.Query("SELECT count(*) FROM character_revised_beast_rewards") is 0L, "旧库夹具不丢弃魔兽奖励事实");
                f.Execute("DROP TABLE character_revised_beast_stage_rewards; DROP TABLE character_revised_beast_rewards");
            }
            bool current = scenario.StartsWith("current-", StringComparison.Ordinal);
            if (!current) f.Execute($"PRAGMA user_version={previousVersion}");
            if (scenario == "conflict") f.Execute("CREATE TABLE character_clear_reward_resets(unknown INTEGER)");
            if (scenario == "late-failure") f.Execute("ALTER TABLE accounts DROP COLUMN disabled");
            if (scenario == "current-weak") f.Execute(DungeonCardRewardSchema.ResetSql.Replace(" ON DELETE CASCADE", "", StringComparison.Ordinal));
            string before = Snapshot(f.Path);
            if (!scenario.StartsWith("upgrade", StringComparison.Ordinal))
            {
                RejectDatabase(() => new CharacterRepository(f.Path));
                Check(Snapshot(f.Path) == before, "奖励刷新迁移冲突、晚期校验失败及当前结构残缺不改变版本、DDL、回执或资产：" + scenario);
                continue;
            }
            string history = f.Snapshot();
            _ = new CharacterRepository(f.Path);
            Check(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                f.Query("SELECT count(*) FROM character_clear_reward_resets") is 0L &&
                f.Query("SELECT count(*) FROM character_npc_bookmarks") is long bookmarks && bookmarks == (previousVersion < 201 ? 0 : 1) &&
                (previousVersion < 201 || f.Query($"SELECT hex(entries) FROM character_npc_bookmarks WHERE character_id={f.Other.Id}") is "11000000") &&
                f.Snapshot() == history,
                "v199/v200连续升级及v201单步升级只追加空刷新账本，已有收藏、冻结牌面、领取位与资产保持：" + scenario);
            string saved = Snapshot(f.Path);
            _ = new CharacterRepository(f.Path);
            Check(Snapshot(f.Path) == saved, "奖励刷新结构重复打开幂等");
            f.Integrity();
        }
    }
}
