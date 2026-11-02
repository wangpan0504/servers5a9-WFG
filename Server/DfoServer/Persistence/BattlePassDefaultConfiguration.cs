using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 这里只选择稳定赛季，启停权威唯一保存在server_events2520；到期不自动换季。
internal sealed record BattlePassDefaultConfiguration(string SeasonKey, DateTimeOffset Start)
{
    internal static BattlePassDefaultConfiguration Resolve(string directory, DateTimeOffset now)
    {
        string path = Path.Combine(directory, "battle-pass-default.json");
        if (File.Exists(path)) return Read(path);
        BattlePassDefaultConfiguration selected;
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "game.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT season_key,start_utc FROM battle_pass_season_rules WHERE start_utc <= $now AND end_utc > $now";
            query.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            var candidates = new List<BattlePassDefaultConfiguration>();
            using (var reader = query.ExecuteReader())
                while (reader.Read()) candidates.Add(new(reader.GetString(0), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))));
            if (candidates.Count > 1) throw new InvalidDataException("多个现有探险记赛季有效，需要显式选择赛季");
            if (candidates.Count == 1) selected = candidates[0];
            else
            {
                query.CommandText = "SELECT EXISTS(SELECT 1 FROM battle_pass_season_rules) OR EXISTS(SELECT 1 FROM account_battle_pass_seasons)";
                if (Convert.ToInt64(query.ExecuteScalar()) != 0)
                    throw new InvalidDataException("已有探险记历史但没有唯一有效赛季，需要显式选择，不自动新建赛季");
                var local = now.ToOffset(TimeSpan.FromHours(8));
                var start = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
                start = start.AddDays(-(((int)start.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
                if (start > local) start = start.AddDays(-7);
                selected = new("local-" + Guid.NewGuid().ToString("N"), start);
            }
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(selected));
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { return Read(path); }
            return selected;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static BattlePassDefaultConfiguration Read(string path)
    {
        var result = JsonSerializer.Deserialize<BattlePassDefaultConfiguration>(File.ReadAllText(path));
        if (result is null || string.IsNullOrWhiteSpace(result.SeasonKey) || result.SeasonKey != result.SeasonKey.Trim())
            throw new InvalidDataException("探险记默认赛季配置无效");
        var local = result.Start.ToOffset(TimeSpan.FromHours(8));
        if (local.DayOfWeek != DayOfWeek.Thursday || local.TimeOfDay != TimeSpan.FromHours(6))
            throw new InvalidDataException("探险记赛季起点必须为北京时间周四06:00");
        return result;
    }
}
