using DfoServer.Game.BattlePass;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void ApplyBattlePassBossRushReward(SqliteConnection db, SqliteTransaction tx, string account,
        SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared, uint? mailId)
    {
        if (battlePassMissionPolicy is not { } policy || prepared.BattlePassBossRushReward is not { } fact) return;
        var at = fact.Clock.GetUtcNow();
        if (ActiveBattlePassMissions(db, tx, policy, at).IsEmpty) return;
        if (at < fact.PlannedAt || !fact.Matches(run, prepared))
            throw new InvalidDataException("灵魂之冢领奖来源与原正式整场结算不一致");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        // 调用点位于原段位奖励写入背包或永久邮件之后，仍与原结算共用同一事务。
        if (!fact.Reward.Delivery.Inventory.Changed && mailId is not > 0)
            throw new InvalidOperationException("灵魂之冢领奖来源缺少原奖励实际交付");
        var before = ReadBattlePassMissionState(db, tx, account, policy, at);
        var eligible = before.Slots.Where(row => BattlePassBossRushReward.MatchesMission(row.Instance.Key.Category, row.Instance.Definition)).ToArray();
        if (eligible.Length == 0) return;
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'boss_rush_rank_reward',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$source", fact.SourceId);
        command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("灵魂之冢领奖来源已经提交");
        int changed = 0;
        foreach (var slot in eligible.Where(row => !row.Completed && row.Count < row.Instance.Definition.RequiredCount))
        {
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = """
                INSERT INTO account_battle_pass_mission_progress(account,season_key,category,period_key,mission_id,current_count)
                VALUES($account,$season,$category,$period,$mission,$next)
                ON CONFLICT(account,season_key,category,period_key,mission_id)
                DO UPDATE SET current_count=$next WHERE current_count=$before
                """;
            update.Parameters.AddWithValue("$account", account); update.Parameters.AddWithValue("$season", policy.SeasonKey);
            update.Parameters.AddWithValue("$category", slot.Instance.Key.Category); update.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
            update.Parameters.AddWithValue("$mission", slot.Instance.Key.Id); update.Parameters.AddWithValue("$before", slot.Count);
            update.Parameters.AddWithValue("$next", checked(slot.Count + 1));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("灵魂之冢领奖任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", checked(before.Season.Revision + 1)); command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("灵魂之冢领奖任务季版本在同事务内变化");
    }
}
