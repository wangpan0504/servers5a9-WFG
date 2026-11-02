using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.BattlePass;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // UTF8规范JSON三元组：版本、赛季、服务器日。固定128bit键沿用来源表32位大写hex合同。
    static string BattlePassDailyLoginSourceId(string season, int day)
    {
        byte[] naturalKey = JsonSerializer.SerializeToUtf8Bytes(new[]
            { "battle-pass-daily-login/v1", season, day.ToString(CultureInfo.InvariantCulture) });
        return Convert.ToHexString(SHA256.HashData(naturalKey).AsSpan(0, 16));
    }

    // 同日冒险团事实不能替代战令日账；只有本次真实登录且本季日账缺失才进入writer。
    bool BattlePassDailyLoginPending(SqliteConnection db, SqliteTransaction? tx, string account,
        int previousDay, int day, DateTimeOffset now)
    {
        if (day < previousDay || battlePassMissionPolicy is not { } policy ||
            !ActiveBattlePassMissions(db, tx, policy, now).Any(row => BattlePassMissionPolicy.IsDailyLogin(row.Definition))) return false;
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT 1 FROM account_battle_pass_source_events WHERE account=$account AND source_kind='daily_login' AND source_id=$source";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$source", BattlePassDailyLoginSourceId(policy.SeasonKey, day));
        return query.ExecuteScalar() is null;
    }

    // 仅由真实登录初始化的原连接/事务调用，不回填历史天数，不自动授予任务经验。
    void ApplyBattlePassDailyLogin(SqliteConnection db, SqliteTransaction tx, string account, int day, DateTimeOffset now)
    {
        if (battlePassMissionPolicy is not { } policy ||
            !ActiveBattlePassMissions(db, tx, policy, now).Any(row => BattlePassMissionPolicy.IsDailyLogin(row.Definition))) return;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'daily_login',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$source", BattlePassDailyLoginSourceId(policy.SeasonKey, day));
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        if (command.ExecuteNonQuery() != 1) return;
        var before = ReadBattlePassMissionState(db, tx, account, policy, now);
        int changed = 0;
        foreach (var slot in before.Slots.Where(row => BattlePassMissionPolicy.IsDailyLogin(row.Instance.Definition) &&
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
            update.Parameters.AddWithValue("$next", checked(slot.Count + 1));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("登录任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", checked(before.Season.Revision + 1));
        command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("登录任务赛季版本在同事务内变化");
    }
}
