using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 本服口径：日/周任务首次XPclaim成功提交才算完成；与原claim共用事务和一次季revision。
    // 唯一claim写入之后才调用，无额外source表、不自动领经验，也不递归赛季任务。
    static void ApplyBattlePassMissionCompletion(SqliteConnection db, SqliteTransaction tx, string account,
        BattlePassMissionState current, byte completedCategory)
    {
        if (completedCategory > 1) return;
        foreach (var slot in current.Slots.Where(row => row.Instance.Key.Category == 2 &&
            IsBattlePassCompletion(row.Instance.Definition, completedCategory) &&
            !row.Completed && row.Count < row.Instance.Definition.RequiredCount))
        {
            using var update = db.CreateCommand(); update.Transaction = tx;
            update.CommandText = """
                INSERT INTO account_battle_pass_mission_progress(account,season_key,category,period_key,mission_id,current_count)
                VALUES($account,$season,2,$period,$mission,$next)
                ON CONFLICT(account,season_key,category,period_key,mission_id)
                DO UPDATE SET current_count=$next WHERE current_count=$before
                """;
            update.Parameters.AddWithValue("$account", account);
            update.Parameters.AddWithValue("$season", current.Season.SeasonKey);
            update.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
            update.Parameters.AddWithValue("$mission", slot.Instance.Key.Id);
            update.Parameters.AddWithValue("$before", slot.Count);
            update.Parameters.AddWithValue("$next", checked(slot.Count + 1));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("完成任务累计在同事务内变化");
        }
    }

    static bool IsBattlePassCompletion(BattlePassMission definition, byte category) =>
        definition.RawTypeCode == 36 && definition.RawParameter == category && definition.Experience == 1600 &&
        (category == 0 && definition.Id == 54 && definition.RequiredCount == 150 ||
         category == 1 && definition.Id == 55 && definition.RequiredCount == 30);
}
