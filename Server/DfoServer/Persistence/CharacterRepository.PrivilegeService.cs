using DfoServer.Game.PrivilegeServices;
using DfoServer.Game.Accounts;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPrivilegeServiceRoster
{
    public ImmutableArray<PrivilegeServiceRosterEntry> ReadPrivilegeServiceRoster(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.slot,COALESCE(p.expires_at,0) FROM characters c
            LEFT JOIN character_privilege_service p ON p.character_id=c.id
            WHERE c.account=$account ORDER BY c.slot;
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        var result = ImmutableArray.CreateBuilder<PrivilegeServiceRosterEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(checked((ushort)reader.GetInt32(0)), reader.GetInt64(1)));
        return result.ToImmutable();
    });

    static PrivilegeServiceState ReadPrivilegeService(SqliteConnection db, SqliteTransaction? transaction, ushort id, string account)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.started_at,p.expires_at,p.purchase_season,p.reward_season,p.last_login_day,
                   p.login_days,p.daily_claim_day,p.claimed_milestones
            FROM character_privilege_service p JOIN characters c ON c.id=p.character_id
            WHERE c.id=$id AND c.account=$account;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return PrivilegeServiceState.Empty;
        var state = new PrivilegeServiceState(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), checked((uint)reader.GetInt64(7)));
        state.Validate();
        return state;
    }

    static void WritePrivilegeService(SqliteConnection db, SqliteTransaction transaction, string account, ushort id,
        PrivilegeServiceState state)
    {
        state.Validate();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        if (state == PrivilegeServiceState.Empty)
        {
            command.CommandText = """
                DELETE FROM character_privilege_service WHERE character_id=$id
                AND EXISTS(SELECT 1 FROM characters WHERE id=$id AND account=$account);
                """;
            command.ExecuteNonQuery();
            return;
        }
        command.CommandText = """
            INSERT INTO character_privilege_service(character_id,started_at,expires_at,purchase_season,reward_season,
                last_login_day,login_days,daily_claim_day,claimed_milestones)
            SELECT id,$started,$expires,$purchaseSeason,$rewardSeason,$day,$days,$claimed,$milestones
            FROM characters WHERE id=$id AND account=$account
            ON CONFLICT(character_id) DO UPDATE SET started_at=excluded.started_at,expires_at=excluded.expires_at,
                purchase_season=excluded.purchase_season,reward_season=excluded.reward_season,
                last_login_day=excluded.last_login_day,login_days=excluded.login_days,
                daily_claim_day=excluded.daily_claim_day,claimed_milestones=excluded.claimed_milestones;
            """;
        command.Parameters.AddWithValue("$started", state.StartedAt);
        command.Parameters.AddWithValue("$expires", state.ExpiresAt);
        command.Parameters.AddWithValue("$purchaseSeason", state.PurchaseSeason);
        command.Parameters.AddWithValue("$rewardSeason", state.RewardSeason);
        command.Parameters.AddWithValue("$day", state.LastLoginDay);
        command.Parameters.AddWithValue("$days", state.LoginDays);
        command.Parameters.AddWithValue("$claimed", state.DailyClaimDay);
        command.Parameters.AddWithValue("$milestones", (long)state.ClaimedMilestones);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("礼遇特权角色归属已变化");
    }
}
