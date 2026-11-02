using System.Text.Json;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void ApplyBattlePassCeraConsumption(SqliteConnection db, SqliteTransaction tx, string account,
        ushort characterId, InventoryPlan plan, InventorySnapshot inventory)
    {
        if (battlePassMissionPolicy is not { } policy || plan.CashShopPayment is not { } fact) return;
        var now = fact.Clock.GetUtcNow();
        if (policy.Active(fact.PlannedAt).IsEmpty || policy.Active(now).IsEmpty) return;
        if (now < fact.PlannedAt || !fact.Matches(plan, inventory))
            throw new InvalidDataException("商城实际点券事实与库存提交不一致");
        long eligible = fact.EligibleCera;
        if (eligible == 0) return;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "SELECT rules_json FROM battle_pass_season_rules WHERE season_key=$season";
        if (command.ExecuteScalar() is not string rules || rules != policy.RulesJson)
            throw new InvalidDataException("商城消费的战令规则已变化");
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var before = ReadBattlePassMissionState(db, tx, account, policy, now);
        command.CommandText = "INSERT INTO account_battle_pass_cera_consumption(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT total_cera,numerator_remainder FROM account_battle_pass_cera_consumption WHERE account=$account AND season_key=$season";
        long oldCera; int oldRemainder;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("商城消费累计账缺失");
            oldCera = reader.GetInt64(0); oldRemainder = reader.GetInt32(1);
        }
        int percentage = policy.Catalog.RawCeraConsumptionExperiencePercentage;
        var delta = BattlePassCeraConsumption.Convert(eligible, percentage, oldRemainder);
        long next = checked(before.Season.Revision + 1), total = checked(oldCera + eligible);
        long experience = checked(before.Season.TotalExperience + delta.Experience);
        command.CommandText = """
            INSERT INTO account_battle_pass_cera_orders
              (account,season_key,order_id,recipient_character_id,cera,eligible_cera,experience,percentage,
               before_remainder,resulting_remainder,before_season_revision,resulting_season_revision,business_snapshot,committed_at)
            VALUES($account,$season,$order,$character,$cera,$eligible,$delta,$percentage,$oldRemainder,$remainder,$before,$next,$business,$at)
            """;
        command.Parameters.AddWithValue("$order", fact.Id.ToString("N"));
        command.Parameters.AddWithValue("$character", characterId); command.Parameters.AddWithValue("$cera", fact.Cera);
        command.Parameters.AddWithValue("$eligible", eligible); command.Parameters.AddWithValue("$delta", delta.Experience);
        command.Parameters.AddWithValue("$percentage", percentage); command.Parameters.AddWithValue("$oldRemainder", oldRemainder);
        command.Parameters.AddWithValue("$remainder", delta.Remainder); command.Parameters.AddWithValue("$before", before.Season.Revision);
        command.Parameters.AddWithValue("$next", next); command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$business", JsonSerializer.Serialize(new { fact.Id, fact.PlannedAt, fact.Cera, fact.TokenCera, fact.HappyTokenCera, fact.Lines }));
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE account_battle_pass_cera_consumption SET total_cera=$total,numerator_remainder=$remainder WHERE account=$account AND season_key=$season AND total_cera=$oldTotal AND numerator_remainder=$oldRemainder";
        command.Parameters.AddWithValue("$total", total); command.Parameters.AddWithValue("$oldTotal", oldCera);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("商城消费累计在同事务中变化");
        command.CommandText = "UPDATE account_battle_pass_seasons SET total_experience=$xp,revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$xp", experience);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("商城消费赛季版本在同事务中变化");
    }
}
