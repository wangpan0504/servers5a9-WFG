using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static PvpMissionState ReadPvpDailyState(SqliteConnection db, SqliteTransaction tx, string account, ushort character,
        PvpMissionPeriod period, PvpMissionState state)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT state_json FROM character_pvp_daily_missions WHERE account=$account AND character_id=$id AND service_day=$day";
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$id", character);
        query.Parameters.AddWithValue("$day", period.Day);
        PvpDailyMissionDay? daily = query.ExecuteScalar() is string json ?
            JsonSerializer.Deserialize<PvpDailyMissionDay>(json) ?? throw new InvalidDataException("决斗日常账本为空") : null;
        daily?.Validate(period.Day);
        // 只消费本角色已经到账的整局事实；时间是Unix秒，不能按毫秒比较或把取消局算成参赛。
        query.CommandText = """
            SELECT r.result_json FROM character_pvp_rating_claims c
            JOIN pvp_arena_match_results r ON r.match_id=c.match_id
            WHERE c.account=$account AND c.character_id=$id AND c.receipt_json IS NOT NULL AND c.outcome<>$cancelled
              AND r.finished_at >= $start AND r.finished_at < $end
            ORDER BY r.finished_at,r.match_id
            """;
        query.Parameters.AddWithValue("$start", period.Start);
        query.Parameters.AddWithValue("$end", period.End);
        query.Parameters.AddWithValue("$cancelled", (int)PvpArenaOutcome.Cancelled);
        var matches = ImmutableArray.CreateBuilder<PvpArenaResultRecord>();
        using (var reader = query.ExecuteReader())
            while (reader.Read())
            {
                var result = JsonSerializer.Deserialize<PvpArenaResultRecord>(reader.GetString(0)) ?? throw new InvalidDataException("决斗日常来源为空");
                result.Validate();
                matches.Add(result);
            }
        return state with { Daily = daily, DailyMatches = matches.ToImmutable() };
    }

    static void WritePvpDailyState(SqliteConnection db, SqliteTransaction tx, string account, PvpMissionPlan plan)
    {
        var daily = plan.DailyAfter ?? throw new InvalidDataException("日常提交缺少快照");
        var period = plan.Period ?? throw new InvalidDataException("日常提交缺少服务日");
        daily.Validate(period.Day);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_pvp_daily_missions(character_id,account,service_day,state_json)
            VALUES($id,$account,$day,$state)
            ON CONFLICT(character_id,service_day) DO UPDATE SET state_json=excluded.state_json WHERE account=excluded.account
            """;
        command.Parameters.AddWithValue("$id", plan.Expected.Id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(daily));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("日常账本账号已变化");
    }
}
