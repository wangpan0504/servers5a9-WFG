using DfoServer.Game.BattlePass;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void ApplyBattlePassGuildContribution(SqliteConnection db, SqliteTransaction tx, string account, int points, long unix)
    {
        if (battlePassMissionPolicy is not { } policy) return;
        var at = DateTimeOffset.FromUnixTimeSeconds(unix);
        if (policy.Active(at).IsEmpty) return;
        if (points <= 0) throw new InvalidDataException("仅原贡献事务的真实正积分可以推进战令");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var before = ReadBattlePassMissionState(db, tx, account, policy, at);
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'guild_contribution',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$source", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会贡献来源已经提交");
        int changed = 0;
        foreach (var slot in before.Slots.Where(row => BattlePassGuildContributionSource.Matches(row.Instance.Definition) &&
            !row.Completed && row.Count < row.Instance.Definition.RequiredCount))
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
            update.Parameters.AddWithValue("$next", BattlePassGuildContributionSource.NextCount(slot.Instance.Definition, slot.Count, points));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会贡献任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", checked(before.Season.Revision + 1)); command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("公会贡献任务赛季版本在同事务内变化");
    }
}
