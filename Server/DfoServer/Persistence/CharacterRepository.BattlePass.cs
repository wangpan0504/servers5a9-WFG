using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public BattlePassSeasonState ReadBattlePass(AccountOwners.Lease owner, string seasonKey) => owner.Execute(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seasonKey);
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            using var exists = db.CreateCommand();
            exists.Transaction = read;
            exists.CommandText = "SELECT 1 FROM account_battle_pass_seasons WHERE account=$account AND season_key=$season";
            exists.Parameters.AddWithValue("$account", owner.Account);
            exists.Parameters.AddWithValue("$season", seasonKey);
            if (exists.ExecuteScalar() is not null) return ReadBattlePassState(db, read, owner.Account, seasonKey);
        }
        using var write = db.BeginWrite("CharacterRepository.ReadBattlePass");
        using var tx = write.Transaction;
        using (var create = db.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                INSERT INTO account_battle_pass_seasons(account,season_key) VALUES($account,$season)
                ON CONFLICT(account,season_key) DO NOTHING
                """;
            create.Parameters.AddWithValue("$account", owner.Account);
            create.Parameters.AddWithValue("$season", seasonKey);
            create.ExecuteNonQuery();
        }
        var state = ReadBattlePassState(db, tx, owner.Account, seasonKey);
        tx.Commit();
        return state;
    });

    public BattlePassRewardCommit CommitBattlePassReward(AccountOwners.Lease owner, Character expected, BattlePassRewardPlan plan) =>
        owner.Execute(() =>
        {
            plan.Before.Validate();
            plan.After.Validate();
            if (plan.Before.SeasonKey != plan.After.SeasonKey ||
                plan.Before.TotalExperience != plan.After.TotalExperience || plan.Before.PassType != plan.After.PassType ||
                plan.Before.Revision != plan.After.Revision || plan.Rewards.IsDefaultOrEmpty ||
                plan.Rewards.Length > 160 || plan.Reward != plan.Rewards[0] ||
                plan.Rewards.Distinct().Count() != plan.Rewards.Length || plan.Before.Claims.Overlaps(plan.Rewards) ||
                !plan.After.Claims.SetEquals(plan.Before.Claims.Union(plan.Rewards)) ||
                plan.Inventory.Reason != "battle_pass_reward" || plan.Inventory.CharacterPrecondition is null ||
                !plan.Inventory.OverflowItems.IsEmpty)
                throw new InvalidOperationException("Invalid battle-pass reward plan.");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CommitBattlePassReward");
            using var tx = write.Transaction;
            if (battlePassAvailable is not null && (!IsBattlePassAvailable() ||
                !DfoServer.Game.Events.ServerEventService.BattlePassScheduleActive(
                    ServerEventRepository.Read(db, tx, DfoServer.Game.Events.ServerEventService.BattlePassId),
                    battlePassClock.GetUtcNow(), battlePassDefaultEnabled))) throw new BattlePassRewardChangedException();
            if (battlePassMissionPolicy is { } policy && (policy.SeasonKey != plan.Before.SeasonKey ||
                ActiveBattlePassMissions(db, tx, policy, battlePassClock.GetUtcNow()).IsEmpty)) throw new BattlePassRewardChangedException();
            var current = ReadBattlePassState(db, tx, owner.Account, plan.Before.SeasonKey);
            if (current.Revision != plan.Before.Revision || current.TotalExperience != plan.Before.TotalExperience ||
                current.PassType != plan.Before.PassType || !current.Claims.SetEquals(plan.Before.Claims))
                throw new BattlePassRewardChangedException();

            var character = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
            long revision = checked(current.Revision + 1);
            using (var update = db.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE account_battle_pass_seasons SET revision=$next
                    WHERE account=$account AND season_key=$season AND revision=$previous
                    """;
                update.Parameters.AddWithValue("$account", owner.Account);
                update.Parameters.AddWithValue("$season", current.SeasonKey);
                update.Parameters.AddWithValue("$next", revision);
                update.Parameters.AddWithValue("$previous", current.Revision);
                if (update.ExecuteNonQuery() != 1) throw new BattlePassRewardChangedException();
            }
            using (var insert = db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO account_battle_pass_claims(account,season_key,level,track,recipient_character_id)
                    VALUES($account,$season,$level,$track,$character)
                    """;
                insert.Parameters.AddWithValue("$account", owner.Account);
                insert.Parameters.AddWithValue("$season", current.SeasonKey);
                var levelParameter = insert.Parameters.Add("$level", SqliteType.Integer);
                var trackParameter = insert.Parameters.Add("$track", SqliteType.Integer);
                insert.Parameters.AddWithValue("$character", character.Id);
                foreach (var reward in plan.Rewards)
                {
                    levelParameter.Value = reward.Level;
                    trackParameter.Value = reward.Track;
                    insert.ExecuteNonQuery();
                }
            }
            var state = ReadBattlePassState(db, tx, owner.Account, current.SeasonKey);
            tx.Commit();
            return new BattlePassRewardCommit(character, state);
        });

    static BattlePassSeasonState ReadBattlePassState(SqliteConnection db, SqliteTransaction tx, string account, string season)
    {
        long experience, revision;
        byte passType;
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                SELECT total_experience,pass_type,revision FROM account_battle_pass_seasons
                WHERE account=$account AND season_key=$season
                """;
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$season", season);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new BattlePassRewardChangedException();
            experience = reader.GetInt64(0);
            passType = checked((byte)reader.GetInt64(1));
            revision = reader.GetInt64(2);
        }
        var claims = ImmutableHashSet.CreateBuilder<BattlePassRewardKey>();
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT level,track FROM account_battle_pass_claims WHERE account=$account AND season_key=$season";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$season", season);
            using var reader = command.ExecuteReader();
            while (reader.Read()) claims.Add(new(checked((byte)reader.GetInt64(1)), reader.GetInt32(0)));
        }
        var state = new BattlePassSeasonState(season, experience, passType, claims.ToImmutable(), revision);
        state.Validate();
        return state;
    }
}
