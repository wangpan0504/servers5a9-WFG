using System.Runtime.CompilerServices;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    sealed record InventoryItemRead(string Database, string Account, ushort CharacterId, long CharacterVersion, long AccountVersion);

    // 只保存本次实际读库/已提交快照的证明，不复制库存；with 派生的快照没有证明，不能沿用旧索引或版本。
    static readonly ConditionalWeakTable<InventorySnapshot, InventoryItemRead> inventoryItemReads = new();

    static bool CanReuseInventoryItems(InventorySnapshot? inventory, InventoryItemRead current) =>
        inventory is not null && inventoryItemReads.TryGetValue(inventory, out var read) && read == current;

    static void RememberInventoryItems(InventorySnapshot inventory, InventoryItemRead read) =>
        inventoryItemReads.AddOrUpdate(inventory, read);

    static InventoryItemRead PrepareInventoryItemRead(SqliteConnection db, SqliteTransaction tx, string account, ushort id,
        InventorySnapshot current, InventorySnapshot committed) =>
        current.Items == committed.Items && inventoryItemReads.TryGetValue(current, out var read) &&
        read.Database == db.DataSource && read.Account == account && read.CharacterId == id
            ? read : ReadInventoryItemRevision(db, tx, account, id);

    static InventoryItemRead ReadInventoryItemRevision(SqliteConnection db, SqliteTransaction tx, string account, ushort id)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT c.inventory_items_revision,COALESCE(a.inventory_items_revision,0)
            FROM characters c LEFT JOIN account_cargo a ON a.account=c.account
            WHERE c.id=$id AND c.account=$account
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("角色不存在");
        return new(db.DataSource, account, id, reader.GetInt64(0), reader.GetInt64(1));
    }
}
