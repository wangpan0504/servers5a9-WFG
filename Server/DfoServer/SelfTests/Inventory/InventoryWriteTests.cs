using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class InventoryWriteTests
{
    public static void Run()
    {
        int start = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-inventory-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("write", () => { });
            var location = new Location(1, 0, 10, 10);
            var current = repository.Create(owner, new("WriteCase", location, location));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            Commit(current.Inventory with { Items = [
                new(0, new(InventoryListType.Main, 121), new(50001, ItemKind.Material, 3) { Rune = 4, EnchantCardId = 123 }),
                new(0, new(InventoryListType.Main, 122), new(50001, ItemKind.Material, 7)),
                new(0, new(InventoryListType.Equipment, 12), new(50002, ItemKind.Equipment, 0)),
                new(0, new(InventoryListType.Avatar, 0), new(50003, ItemKind.Avatar, 0))
                    { Avatar = new() { Socket0 = AvatarSocket.Empty(3), Color1 = 12 } },
                new(0, new(InventoryListType.Pet, 0), new(50004, ItemKind.Creature, 0))
                    { Creature = new() { Name = "宠物\"\\<>&", Mode = 1, Mode1Field0 = 3, ExpireTime = 123456 } }
            ], AccountCargo = new(0, 8, 0) }, "allocate");
            TestCheck.That(current.Inventory.Items.Where(item => item.Avatar is not null || item.Creature is not null).All(item => item.Core.Value == item.Uid && item.Uid > 0), "新详情实例的最终 UID 同时进入核心与审计");

            // 传入顺序与数据库不一致，比较不能把数组顺序当资产变化。
            current = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.Reverse().ToImmutableArray() } };
            Commit(current.Inventory with { Items = current.Inventory.Items.Select(item => item.Position.List switch
            {
                InventoryListType.Main => item with { Position = item.Position with { Index = (short)(243 - item.Position.Index) } },
                InventoryListType.Avatar or InventoryListType.Pet => item with { Position = item.Position with { Index = 1 } },
                _ => item
            }).ToImmutableArray() }, "move");
            Commit(current.Inventory with { Items = current.Inventory.Items.Select(item => item.Position.List switch
            {
                InventoryListType.Main => item with { Core = item.Core with { Rune = 6, SortLockFlag = 1 } },
                InventoryListType.Avatar => item with { Avatar = item.Avatar! with { Color2 = 17 } },
                InventoryListType.Pet => item with { Creature = item.Creature! with { Name = "改名\"<>", Satiety = 42 } },
                _ => item
            }).ToImmutableArray() }, "attributes");
            Commit(current.Inventory with { Items = current.Inventory.Items.Select(item => item.Position == new InventorySlot(InventoryListType.Main, 121)
                ? item with { Position = new(InventoryListType.AccountCargo, 0) } : item).ToImmutableArray() }, "transfer");
            current = current with { Inventory = VerifyReadReuse(db, current) };
            var unchangedItems = current.Inventory.Items;
            long unchangedItemVersion = Scalar("SELECT inventory_items_revision FROM characters WHERE id=" + current.Id);
            Commit(current.Inventory with { Gold = 123 }, "gold_only");
            TestCheck.That(current.Inventory.Items == unchangedItems &&
                Scalar("SELECT inventory_items_revision FROM characters WHERE id=" + current.Id) == unchangedItemVersion &&
                Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='gold_only'") == 0,
                "只改金币不复制物品数组、不写实例槽位或消费审计");
            Commit(current.Inventory with { Gold = 0 }, "gold_restore");

            var original = current.Inventory;
            foreach (var changed in new InventorySnapshot[]
            {
                original with { Gold = 1 }, original with { Expansion = 8 }, original with { ReviveCoins = 1 },
                original with { WinPoints = 1 }, original with { GoldCarryLimit = 1 }, original with { AuctionGoldLimit = 1 },
                original with { AccountWallet = original.AccountWallet.Set(CurrencyKind.Cera, 1) },
                original with { CargoCapacity = 16 }, original with { AccountCargo = original.AccountCargo with { Gold = 1 } },
                original with { EpicPieces = original.EpicPieces with { Revision = original.EpicPieces.Revision + 1 } },
                original with { ShopPurchases = [new(1, 50001, false, false, 0, 1)] },
                original with { Items = original.Items.RemoveAt(0) },
                original with { Items = original.Items.SetItem(0, original.Items[0] with { Core = original.Items[0].Core with { Rune = 8 } }) },
                original with { Items = original.Items.SetItem(0, original.Items[1]) }
            })
                Reject<InvalidOperationException>(() => repository.CommitInventory(owner, current with { Inventory = changed }, new(original, original, "mismatch")), "完整资产前提不符仍拒绝");

            using (var writer = db.BeginTransaction(deferred: false))
            {
                var same = current.Inventory with { Items = current.Inventory.Items.Reverse().ToImmutableArray() };
                var unchanged = repository.CommitInventory(owner, current, new(same, same, "noop"));
                TestCheck.That(ReferenceEquals(current, unchanged), "无变化计划复验成功，不申请 writer 或新增审计");
                writer.Rollback();
            }

            long audits = Scalar("SELECT count(*) FROM inventory_audit_log");
            long itemVersion = Scalar("SELECT inventory_items_revision FROM characters WHERE id=" + current.Id);
            string saved = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            Sql("CREATE TRIGGER fail_write_audit BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'audit failure'); END");
            var failed = current.Inventory with { Gold = 1, Items = current.Inventory.Items.Add(new(0, new(InventoryListType.Avatar, 2), new(50003, ItemKind.Avatar, 0)) { Avatar = new() }) };
            Reject<SqliteException>(() => repository.CommitInventory(owner, current, new(current.Inventory, failed, "failed")), "编码复用不绕过事务内审计失败");
            Sql("DROP TRIGGER fail_write_audit");
            TestCheck.That(Scalar("SELECT inventory_items_revision FROM characters WHERE id=" + current.Id) == itemVersion,
                "末段审计失败同时回滚物品变更标记");
            _ = ReadInventory(db, current, current.Inventory, reuse: true);
            TestCheck.That(JsonSerializer.Serialize(new CharacterRepository(path).LoadCharacter(owner, 0)) == saved && Scalar("SELECT count(*) FROM inventory_audit_log") == audits,
                "后期失败回滚物品、详情、余额、版本及审计");

            Commit(current.Inventory with { Items = current.Inventory.Items.Where(item => item.Avatar is null && item.Creature is null).ToImmutableArray() }, "delete_details");
            TestCheck.That(Scalar("SELECT count(*) FROM inventory_avatar_details") == 0 && Scalar("SELECT count(*) FROM inventory_creature_details") == 0, "删除详情级联且保留旧审计摘要");
            var stale = current;
            var external = current.Inventory.Items.Single(item => item.Position.List == InventoryListType.Main);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "UPDATE character_inventory_items SET item_core=$core WHERE item_uid=$uid";
                command.Parameters.AddWithValue("$core", JsonSerializer.Serialize(external.Core with { EnchantCardId = 777 }));
                command.Parameters.AddWithValue("$uid", external.Uid);
                command.ExecuteNonQuery();
            }
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, stale, new(stale.Inventory, stale.Inventory with { Gold = 1 }, "stale")), "外部同 revision 改核心仍在事务内重新比较并拒绝");

            void Commit(InventorySnapshot after, string reason)
            {
                var before = current.Inventory;
                current = repository.CommitInventory(owner, current, new(before, after, reason));
                var slots = before.Items.Select(item => item.Position).Union(current.Inventory.Items.Select(item => item.Position))
                    .Where(slot => before.Find(slot) != current.Inventory.Find(slot)).OrderBy(slot => slot.List).ThenBy(slot => slot.Index).ToArray();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT list_type,slot_index,item_uid_before,item_uid_after,core_hash_before,core_hash_after FROM inventory_audit_log WHERE action_name=$reason ORDER BY list_type,slot_index";
                command.Parameters.AddWithValue("$reason", reason);
                using (var reader = command.ExecuteReader())
                {
                    foreach (var slot in slots)
                    {
                        var old = before.Find(slot); var item = current.Inventory.Find(slot);
                        TestCheck.That(reader.Read() && reader.GetInt32(0) == (int)slot.List && reader.GetInt32(1) == slot.Index, "审计精确覆盖全部变化槽并有序");
                        TestCheck.That((reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2)) == old?.Uid && (reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3)) == item?.Uid, "审计 UID 使用事务前状态和实际提交结果");
                        TestCheck.That((reader.IsDBNull(4) ? null : reader.GetString(4)) == ReferenceHash(old) && (reader.IsDBNull(5) ? null : reader.GetString(5)) == ReferenceHash(item), "核心及详情摘要与原匿名对象编码逐字节一致");
                    }
                    TestCheck.That(!reader.Read(), "无多余变化审计");
                }
                foreach (var item in current.Inventory.Items)
                {
                    command.Parameters.Clear(); command.Parameters.AddWithValue("$uid", item.Uid);
                    command.CommandText = "SELECT i.item_core,a.detail,p.detail FROM character_inventory_items i LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid WHERE i.item_uid=$uid";
                    using var reader = command.ExecuteReader();
                    TestCheck.That(reader.Read() && reader.GetString(0) == JsonSerializer.Serialize(item.Core) &&
                        (reader.IsDBNull(1) ? null : reader.GetString(1)) == (item.Avatar is null ? null : JsonSerializer.Serialize(item.Avatar)) &&
                        (reader.IsDBNull(2) ? null : reader.GetString(2)) == (item.Creature is null ? null : JsonSerializer.Serialize(item.Creature)), "落库完整 JSON 与已提交对象一致");
                }
                var reloaded = new CharacterRepository(path).LoadCharacter(owner, 0);
                TestCheck.That(reloaded.Revision == current.Revision && reloaded.Inventory.Items.SequenceEqual(current.Inventory.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index)), "重开库保留全部 UID、完整核心、详情和位置");
            }
            long Scalar(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - start} 库存比较、编码复用与独立审计断言");
    }
    static string? ReferenceHash(InventoryItem? item) => item is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        item.Avatar is null && item.Creature is null ? JsonSerializer.Serialize(item.Core) : JsonSerializer.Serialize(new { item.Core, item.Avatar, item.Creature }))));

    static InventorySnapshot VerifyReadReuse(SqliteConnection db, DfoServer.Game.Characters.Character character)
    {
        var original = character.Inventory;
        var current = ReadInventory(db, character, original, reuse: true);
        var derived = current with { Items = current.Items.SetItem(0, current.Items[0] with { Core = current.Items[0].Core with { Marker16 = 999 } }) };
        var checkedInventory = ReadInventory(db, character, derived, reuse: false);
        TestCheck.That(checkedInventory.Items.SequenceEqual(original.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index)),
            "with 派生快照不能继承原读取证明");
        current = checkedInventory;
        foreach (var item in original.Items)
        {
            string table = item.Avatar is not null ? "inventory_avatar_details" :
                item.Creature is not null ? "inventory_creature_details" : "character_inventory_items";
            string field = table == "character_inventory_items" ? "item_core" : "detail";
            string saved;
            using (var command = db.CreateCommand())
            {
                command.CommandText = $"SELECT {field} FROM {table} WHERE item_uid=$uid";
                command.Parameters.AddWithValue("$uid", item.Uid);
                saved = (string)command.ExecuteScalar()!;
            }
            string changed = item.Avatar is not null ? JsonSerializer.Serialize(item.Avatar with { Color1 = 99 }) :
                item.Creature is not null ? JsonSerializer.Serialize(item.Creature with { Name = "Changed" }) :
                JsonSerializer.Serialize(item.Core with { Marker16 = 999 });
            Update(changed);
            var actual = ReadInventory(db, character, current, reuse: false);
            TestCheck.That(actual.Find(item.Position) != current.Find(item.Position), "同角色revision修改核心、详情或账号仓库仍强制重读");
            Update(saved);
            current = ReadInventory(db, character, actual, reuse: false);
            TestCheck.That(current.Items.SequenceEqual(original.Items.OrderBy(row => row.Position.List).ThenBy(row => row.Position.Index)),
                "还原字段后完整属性和归属保持不变");
            void Update(string json)
            {
                using var command = db.CreateCommand();
                command.CommandText = $"UPDATE {table} SET {field}=$json WHERE item_uid=$uid";
                command.Parameters.AddWithValue("$uid", item.Uid);
                command.Parameters.AddWithValue("$json", json);
                command.ExecuteNonQuery();
            }
        }
        var source = current.Items.Single(item => item.Position.List == InventoryListType.Avatar);
        Move(2);
        var moved = ReadInventory(db, character, current, reuse: false);
        TestCheck.That(moved.Find(source.Position) is null && moved.Find(new(InventoryListType.Avatar, 2))?.Uid == source.Uid,
            "同revision槽位变化失效旧读取证明");
        Move(source.Position.Index);
        current = ReadInventory(db, character, moved, reuse: false);
        using (var rollback = db.BeginTransaction())
        {
            using var command = db.CreateCommand();
            command.Transaction = rollback;
            command.CommandText = "UPDATE character_inventory_items SET updated_at='rollback' WHERE item_uid=$uid";
            command.Parameters.AddWithValue("$uid", source.Uid);
            command.ExecuteNonQuery();
            rollback.Rollback();
        }
        _ = ReadInventory(db, character, current, reuse: true);
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT revision FROM characters WHERE id=$id";
            command.Parameters.AddWithValue("$id", character.Id);
            TestCheck.That(Convert.ToInt64(command.ExecuteScalar()) == character.Revision, "物品变更标记不改变角色业务revision");
        }
        return current;
        void Move(short index)
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE character_inventory_slots SET slot_index=$index WHERE item_uid=$uid";
            command.Parameters.AddWithValue("$uid", source.Uid);
            command.Parameters.AddWithValue("$index", index);
            command.ExecuteNonQuery();
        }
    }

    static InventorySnapshot ReadInventory(SqliteConnection db, DfoServer.Game.Characters.Character character, InventorySnapshot expected, bool reuse)
    {
        int itemReads = 0;
        SQLitePCL.strdelegate_authorizer authorize = (_, action, table, _, _, _) =>
        {
            if (action != SQLitePCL.raw.SQLITE_READ || table is not ("character_inventory_items" or "character_inventory_slots" or
                "character_title_book_slots" or "inventory_avatar_details" or "inventory_creature_details" or "mailbox_attachments"))
                return SQLitePCL.raw.SQLITE_OK;
            itemReads++;
            return reuse ? SQLitePCL.raw.SQLITE_DENY : SQLitePCL.raw.SQLITE_OK;
        };
        SQLitePCL.raw.sqlite3_set_authorizer(db.Handle, authorize, null);
        InventorySnapshot actual;
        try
        {
            using var read = db.BeginTransaction(deferred: true);
            var method = typeof(CharacterRepository).GetMethod("ReadInventory", BindingFlags.NonPublic | BindingFlags.Static)!;
            actual = (InventorySnapshot)method.Invoke(null, [db, character.Id, read, null, expected])!;
        }
        finally { SQLitePCL.raw.sqlite3_set_authorizer(db.Handle, (SQLitePCL.strdelegate_authorizer)null!, null); }
        TestCheck.That(reuse ? itemReads == 0 && actual.Items == expected.Items : itemReads > 0,
            "物品未变时禁止查询任何实例、槽位或详情表，变化或无证明时完整读取");
        return actual;
    }
}
