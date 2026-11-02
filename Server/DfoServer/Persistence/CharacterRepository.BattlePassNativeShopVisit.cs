using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    BattlePassMissionCommit CommitNativeShopVisit(AccountOwners.Lease owner, Character expected, long seasonRevision,
        DateTimeOffset sampledAt, ushort key, uint reportedCount, uint auxiliary, TimeProvider clock)
    {
        if (battlePassMissionPolicy is not { } policy || key != 11 || reportedCount == 0 || auxiliary != 0)
            return BattlePassMissionCommit.Reject();
        var requested = policy.Active(sampledAt).SingleOrDefault(row => row.Key.Category == 0 &&
            row.Key.Id == 11 && BattlePassMissionPolicy.IsNativeShopVisit(row.Definition));
        if (requested is null) return BattlePassMissionCommit.Reject();
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitNativeShopVisit");
        using var tx = write.Transaction;
        var now = clock.GetUtcNow();
        if (now < sampledAt) return BattlePassMissionCommit.Reject();
        var character = Require(db, tx, owner.Account, expected.Name);
        if (character.Id != expected.Id || character.Slot != expected.Slot || character.Revision != expected.Revision)
            return BattlePassMissionCommit.Reject();
        var current = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        if (current.Season.Revision != seasonRevision) return BattlePassMissionCommit.Reject();
        var slot = current.Slots.SingleOrDefault(row => row.Instance.Key == requested.Key &&
            BattlePassMissionPolicy.IsNativeShopVisit(row.Instance.Definition));
        if (slot is null) return BattlePassMissionCommit.Reject();
        if (slot.Completed || slot.Count >= 1)
        {
            tx.Commit();
            return new BattlePassMissionCommit(true, 0, current);
        }
        byte[] naturalKey = JsonSerializer.SerializeToUtf8Bytes(new[] { "native-shop-visit/v1", policy.SeasonKey,
            slot.Instance.Key.Period.ToString(CultureInfo.InvariantCulture) });
        string source = Convert.ToHexString(SHA256.HashData(naturalKey).AsSpan(0, 16));
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.Parameters.AddWithValue("$source", source); command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'native_shop_visit',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        if (command.ExecuteNonQuery() == 0)
        {
            tx.Commit();
            return new BattlePassMissionCommit(true, 0, current);
        }
        command.CommandText = """
            INSERT INTO account_battle_pass_mission_progress(account,season_key,category,period_key,mission_id,current_count)
            VALUES($account,$season,0,$period,11,1)
            ON CONFLICT(account,season_key,category,period_key,mission_id)
            DO UPDATE SET current_count=1 WHERE current_count=0
            """;
        command.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("商城访问任务进度在同事务内变化");
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$before", current.Season.Revision);
        command.Parameters.AddWithValue("$next", checked(current.Season.Revision + 1));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("商城访问任务赛季版本在同事务内变化");
        var committed = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        tx.Commit();
        return new BattlePassMissionCommit(true, 0, committed);
    }
}
