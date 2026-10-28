using System.Text.Json;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 账号皮肤仓只保存 [action target] 为 [account] 的皮肤所有权；选中项仍在角色存档里。
    static SkinStorageState ReadAccountSkins(SqliteConnection db, string account, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state_json FROM account_skin_storage WHERE account=$account";
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? JsonSerializer.Deserialize<SkinStorageState>(reader.GetString(0)) ?? throw new InvalidDataException("账号皮肤仓状态缺失")
            : SkinStorageState.Empty;
    }

    static InventorySnapshot WriteAccountSkins(SqliteConnection db, SqliteTransaction transaction, string account,
        InventoryPlan plan, InventorySnapshot after)
    {
        if (!plan.AccountSkinsChanged) return after;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(after.AccountSkins));
        command.CommandText = "INSERT INTO account_skin_storage(account,state_json) VALUES($account,$state) " +
            "ON CONFLICT(account) DO UPDATE SET state_json=excluded.state_json";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("账号皮肤仓写入失败");
        return after;
    }
}
