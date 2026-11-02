using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.PcRoom;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPcRoomStore
{
    public PcRoomClaimState ReadPcRoomClaims(AccountOwners.Lease owner, ushort characterId, int day) => owner.Execute(() =>
    {
        if (day < 0) throw new ArgumentOutOfRangeException(nameof(day));
        using var db = database.Open();
        using var transaction = db.BeginRead();
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT COALESCE(a.claim_day,-1),COALESCE(p.claim_day,-1)
            FROM characters c
            LEFT JOIN account_pcroom_claims a ON a.account=c.account
            LEFT JOIN character_pcroom_claims p ON p.character_id=c.id AND p.account=c.account
            WHERE c.id=$id AND c.account=$account
            """;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", owner.Account);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("网吧奖励角色不属于当前账号");
        return new PcRoomClaimState(reader.GetInt64(0) >= day, reader.GetInt64(1) >= day);
    });

    static void WritePcRoomClaim(SqliteConnection db, SqliteTransaction transaction, string account,
        ushort characterId, InventoryPlan plan)
    {
        if (plan.PcRoomClaim is not { } claim) return;
        if (claim.Day < 0 || !plan.Changed)
            throw new InvalidDataException("网吧领奖必须包含实际奖励资产");
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = claim.Rental ? """
            INSERT INTO character_pcroom_claims(character_id,account,claim_day) VALUES($id,$account,$day)
            ON CONFLICT(character_id) DO UPDATE SET claim_day=excluded.claim_day
            WHERE character_pcroom_claims.account=excluded.account AND character_pcroom_claims.claim_day<excluded.claim_day
            """ : """
            INSERT INTO account_pcroom_claims(account,claim_day) VALUES($account,$day)
            ON CONFLICT(account) DO UPDATE SET claim_day=excluded.claim_day
            WHERE account_pcroom_claims.claim_day<excluded.claim_day
            """;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$day", claim.Day);
        if (query.ExecuteNonQuery() != 1) throw new PcRoomAlreadyClaimedException();
    }
}
