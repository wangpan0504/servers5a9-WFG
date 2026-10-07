using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static ImmutableArray<ShopPurchaseCount> ReadShopPurchases(SqliteConnection db, string account, ushort characterId, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT npc_id,item_id,character_id,reset_daily,day,count FROM shop_purchase_counts
            WHERE account=$account AND character_id=$id ORDER BY npc_id,item_id,character_id,reset_daily;
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", characterId);
        var rows = ImmutableArray.CreateBuilder<ShopPurchaseCount>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2) == 0, reader.GetBoolean(3), reader.GetInt32(4), reader.GetInt32(5)));
        return rows.ToImmutable();
    }

    static void WriteShopPurchases(SqliteConnection db, SqliteTransaction transaction, string account, ushort characterId,
        InventorySnapshot before, InventorySnapshot after)
    {
        if (before.ShopPurchases.Any(old => !after.ShopPurchases.Any(old.SameKey)))
            throw new InvalidOperationException("商店业务不能删除既有限购记录");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shop_purchase_counts(account,character_id,npc_id,item_id,reset_daily,day,count)
            VALUES($account,$id,$npc,$item,$daily,$day,$count)
            ON CONFLICT(account,character_id,npc_id,item_id,reset_daily) DO UPDATE SET day=excluded.day,count=excluded.count;
            """;
        foreach (var row in after.ShopPurchases.Where(row => !before.ShopPurchases.Contains(row)))
        {
            // CommitInventory 已在同一写事务内核对角色 revision 与完整旧限购快照。
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$id", row.AccountWide ? 0 : characterId);
            command.Parameters.AddWithValue("$npc", row.NpcId);
            command.Parameters.AddWithValue("$item", row.ItemId);
            command.Parameters.AddWithValue("$daily", row.ResetDaily);
            command.Parameters.AddWithValue("$day", row.Day);
            command.Parameters.AddWithValue("$count", row.Count);
            command.ExecuteNonQuery();
        }
    }
}
