using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static AccountCargoState ReadAccountCargo(SqliteConnection db, string account, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision,capacity,gold FROM account_cargo WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), checked((ushort)reader.GetInt32(1)), reader.GetInt32(2)) : new();
    }

    static InventorySnapshot WriteCargo(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        ushort characterId, InventoryPlan plan, InventorySnapshot after)
    {
        var before = plan.Before;
        string reason = plan.Reason;
        if (before.CargoCapacity == after.CargoCapacity && !plan.AccountCargoChanged)
            return after;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$personal", after.CargoCapacity);
        command.CommandText = "UPDATE characters SET cargo_capacity=$personal WHERE id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("仓库角色所有权变化");
        if (plan.AccountCargoChanged)
        {
            command.Parameters.AddWithValue("$revision", before.AccountCargo.Revision);
            command.Parameters.AddWithValue("$capacity", after.AccountCargo.Capacity);
            command.Parameters.AddWithValue("$gold", after.AccountCargo.Gold);
            command.CommandText = "UPDATE account_cargo SET revision=revision+1,capacity=$capacity,gold=$gold WHERE account=$account AND revision=$revision";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("账号仓库版本变化");
            after = after with { AccountCargo = after.AccountCargo with { Revision = before.AccountCargo.Revision + 1 } };
        }
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$personalBefore", before.CargoCapacity);
        command.Parameters.AddWithValue("$accountBefore", before.AccountCargo.Capacity);
        command.Parameters.AddWithValue("$accountAfter", after.AccountCargo.Capacity);
        command.Parameters.AddWithValue("$goldBefore", before.AccountCargo.Gold);
        command.Parameters.AddWithValue("$goldAfter", after.AccountCargo.Gold);
        command.CommandText = """
            INSERT INTO cargo_audit_log(account,character_id,generation,action_name,personal_capacity_before,personal_capacity_after,
              account_capacity_before,account_capacity_after,account_gold_before,account_gold_after)
            VALUES($account,$id,$generation,$reason,$personalBefore,$personal,$accountBefore,$accountAfter,$goldBefore,$goldAfter);
            """;
        command.ExecuteNonQuery();
        return after;
    }
}
