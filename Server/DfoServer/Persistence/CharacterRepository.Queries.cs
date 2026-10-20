using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        // 延迟只读事务保证身份与外观属于同一数据库快照，不占用 writer。
        using var transaction = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = "SELECT id,slot,name,job,grow_type,level FROM characters WHERE account=$account ORDER BY slot";
        var rows = new List<CharacterRosterEntry>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add(new(checked((ushort)reader.GetInt32(0)), checked((ushort)reader.GetInt32(1)), reader.GetString(2),
                    checked((byte)reader.GetInt32(3)), checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5)), []));
        if (rows.Count == 0)
            return [];
        command.CommandText = """
            SELECT i.item_uid,s.list_type,s.slot_index,i.item_core,a.detail,p.detail,i.character_id
            FROM character_inventory_items i
            JOIN characters c ON c.id=i.character_id AND c.account=i.account
            JOIN character_inventory_slots s ON s.item_uid=i.item_uid AND s.account=i.account AND s.character_id=i.character_id
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid
            WHERE i.account=$account AND s.list_type=3 ORDER BY c.slot,s.slot_index;
            """;
        var equipment = new Dictionary<ushort, ImmutableArray<InventoryItem>.Builder>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ushort id = checked((ushort)reader.GetInt32(6));
                if (!equipment.TryGetValue(id, out var items))
                    equipment.Add(id, items = ImmutableArray.CreateBuilder<InventoryItem>());
                items.Add(ReadInventoryItem(reader));
            }
        return rows.Select(row =>
        {
            var items = equipment.TryGetValue(row.Id, out var builder) ? builder.ToImmutable() : [];
            new InventorySnapshot(items).Validate();
            return row with { Equipment = items };
        }).ToArray();
    });

    public Character LoadCharacter(AccountOwners.Lease owner, ushort slot) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM characters WHERE account=$account AND slot=$slot";
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$slot", slot);
        Character character;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidDataException("Slot not found");
            character = Read(reader);
        }
        return ReadProgress(db, character, transaction);
    });

    // 仅在一次完整列表读取中共享，不跨请求缓存账号余额、限购或仓库。
    sealed record AccountInventory(AccountWallet Wallet, AccountCargoState Cargo, EpicPieceBook Pieces,
        ImmutableArray<InventoryItem> Items, ImmutableArray<ShopPurchaseCount> Purchases);

    static AccountInventory ReadAccountInventory(SqliteConnection db, string account, SqliteTransaction? transaction) =>
        new(ReadAccountWallet(db, account, transaction), ReadAccountCargo(db, account, transaction),
            ReadEpicPieces(db, account, transaction), ReadInventoryItems(db, account, null, transaction),
            ReadShopPurchases(db, account, 0, transaction));
}
