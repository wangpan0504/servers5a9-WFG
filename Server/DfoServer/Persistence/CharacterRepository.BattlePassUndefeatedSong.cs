using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void ApplyBattlePassUndefeatedSong(SqliteConnection db, SqliteTransaction tx, string account,
        PreparedRaidReward prepared, InventoryPlan inventory, DateTimeOffset now, int? settledSong = null)
    {
        if (battlePassMissionPolicy is not { } policy ||
            !ActiveBattlePassMissions(db, tx, policy, now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition))) return;
        int acquired = settledSong ?? BattlePassUndefeatedSongSource.DirectAmount(prepared, inventory, now);
        if (acquired == 0) return;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var before = ReadBattlePassMissionState(db, tx, account, policy, now);
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,$kind,$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$kind", BattlePassUndefeatedSongSource.SourceKind);
        command.Parameters.AddWithValue("$source", BattlePassUndefeatedSongSource.SourceId(prepared));
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("不渝之咏原攻坚来源已经提交");
        int changed = 0;
        foreach (var slot in before.Slots.Where(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition) &&
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
            update.Parameters.AddWithValue("$next", BattlePassUndefeatedSongSource.NextCount(slot.Instance.Definition, slot.Count, acquired));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("不渝之咏任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", checked(before.Season.Revision + 1)); command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("不渝之咏任务赛季版本在同事务内变化");
    }
}
