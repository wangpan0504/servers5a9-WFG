using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 只参加原库存writer；不建第二事务，不直接发经验，也不改任务领取事实。
    void ApplyBattlePassEnhancement(SqliteConnection db, SqliteTransaction tx, string account,
        InventoryPlan plan, InventorySnapshot inventory)
    {
        if (battlePassMissionPolicy is not { } policy || plan.EquipmentEnhancement is not { } fact) return;
        var at = fact.Clock.GetUtcNow();
        if (policy.Active(at).IsEmpty) return;
        if (at < fact.PlannedAt || !fact.Matches(plan, inventory))
            throw new InvalidDataException("普通装备升级来源与实际库存事务不一致");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var before = ReadBattlePassMissionState(db, tx, account, policy, at);
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'equipment_enhancement',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$source", fact.Id.ToString("N")); command.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        // 库存revision已拒绝旧计划；若来源仍被重用，整笔拒绝，不能仅跳过计量后再次扣材料。
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("普通装备升级来源已经提交");
        int changed = 0;
        foreach (var slot in before.Slots.Where(row => BattlePassEnhancementSource.Matches(row.Instance.Definition, fact) &&
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
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("装备升级任务进度在同事务内变化");
            changed++;
        }
        if (changed == 0) return;
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$next", checked(before.Season.Revision + 1)); command.Parameters.AddWithValue("$before", before.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("装备升级任务赛季版本在同事务内变化");
    }
}
