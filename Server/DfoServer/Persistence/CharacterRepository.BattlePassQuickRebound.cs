using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public BattlePassMissionCommit CommitBattlePassQuickRebound(AccountOwners.Lease owner, Character expected,
        DungeonEntryPlan entry, DungeonRunIdentity identity, BattlePassQuickReboundObservation use, TimeProvider clock) => owner.Execute(() =>
    {
        if (battlePassMissionPolicy is not { } policy ||
            !BattlePassQuickReboundSource.Eligible(expected, entry, identity, use))
            return BattlePassMissionCommit.Reject();
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitBattlePassQuickRebound");
        using var tx = write.Transaction;
        var now = clock.GetUtcNow();
        var requested = ActiveBattlePassMissions(db, tx, policy, now)
            .SingleOrDefault(row => row.Key.Category == 0 && row.Key.Id == 5 &&
                BattlePassQuickReboundSource.Matches(row.Definition));
        if (requested is null) return BattlePassMissionCommit.Reject();
        var character = Require(db, tx, owner.Account, expected.Name);
        if (character.Id != expected.Id || character.Slot != expected.Slot || character.Revision != expected.Revision ||
            !BattlePassQuickReboundSource.Eligible(character, entry, identity, use))
            return BattlePassMissionCommit.Reject();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season) ON CONFLICT(account,season_key) DO NOTHING";
        command.ExecuteNonQuery();
        var current = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        var slot = current.Slots.SingleOrDefault(row => row.Instance.Key == requested.Key &&
            BattlePassQuickReboundSource.Matches(row.Instance.Definition));
        if (slot is null || slot.Completed || slot.Count >= 1)
            return BattlePassMissionCommit.Reject();
        string source = BattlePassQuickReboundSource.SourceKey(policy.SeasonKey, slot.Instance.Key.Period);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.CommandText = """
            INSERT INTO account_battle_pass_source_events(account,source_kind,source_id,season_key,occurred_at)
            VALUES($account,'quick_rebound_skill',$source,$season,$at)
            ON CONFLICT(account,source_kind,source_id) DO NOTHING
            """;
        if (command.ExecuteNonQuery() == 0)
            return BattlePassMissionCommit.Reject();
        command.CommandText = """
            INSERT INTO account_battle_pass_mission_progress(account,season_key,category,period_key,mission_id,current_count)
            VALUES($account,$season,0,$period,5,1)
            ON CONFLICT(account,season_key,category,period_key,mission_id)
            DO UPDATE SET current_count=1 WHERE current_count=0
            """;
        command.Parameters.AddWithValue("$period", slot.Instance.Key.Period);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("受身蹲伏任务进度在同事务内变化");
        command.CommandText = "UPDATE account_battle_pass_seasons SET revision=$next WHERE account=$account AND season_key=$season AND revision=$before";
        command.Parameters.AddWithValue("$before", current.Season.Revision);
        command.Parameters.AddWithValue("$next", checked(current.Season.Revision + 1));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("受身蹲伏任务赛季版本在同事务内变化");
        var committed = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        tx.Commit();
        return new BattlePassMissionCommit(true, 0, committed);
    });
}
