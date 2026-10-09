using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Currency;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => owner.Execute(() =>
    {
        ValidateInventoryPlan(expected, plan);
        if (!plan.Changed)
            return expected;
        using var db = database.Open();
        using var transaction = db.BeginTransaction(deferred: false);
        var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan);
        transaction.Commit();
        return character;
    });

    static void ValidateInventoryPlan(Character expected, InventoryPlan plan)
    {
        if (!SameInventory(expected.Inventory, plan) || plan.CharacterPrecondition?.Matches(expected) == false)
            throw new InvalidOperationException("背包规划不是当前快照");
        plan.After.Validate(allocating: true);
        if (plan.After.AccountWallet.Revision != plan.Before.AccountWallet.Revision || plan.After.AccountCargo.Revision != plan.Before.AccountCargo.Revision ||
            plan.After.EpicPieces.Revision != plan.Before.EpicPieces.Revision)
            throw new InvalidOperationException("业务不能修改账号资产版本");
    }

    Character CommitInventoryInTransaction(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        Character expected, InventoryPlan plan, IReadOnlySet<long>? retained = null, IReadOnlyDictionary<long, InventoryItem>? incoming = null)
    {
        ValidateInventoryPlan(expected, plan);
        var current = Require(db, transaction, owner.Account, expected.Name);
        // GM进度修改不递增revision；库存和规划资格都必须在写事务内重新核对。
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !SameInventory(current.Inventory, plan) || plan.CharacterPrecondition?.Matches(current) == false)
            throw new InvalidOperationException("角色库存、规划资格或owner/revision已变化");
        if (!plan.Changed)
            return current;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET revision=revision+1,gold=$gold,inventory_expansion=$expansion
            WHERE id=$id AND account=$account AND revision=$revision;
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$gold", (long)plan.After.Gold);
        command.Parameters.AddWithValue("$expansion", plan.After.Expansion);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Character revision or owner changed");
        var inventory = WriteInventory(db, transaction, owner, expected.Id, plan, plan.After, retained, incoming);
        inventory = WriteWallet(db, transaction, owner, expected.Id, plan, inventory);
        inventory = WriteCargo(db, transaction, owner, expected.Id, plan, inventory);
        WriteShopPurchases(db, transaction, owner.Account, expected.Id, plan.Before, inventory);
        inventory = WriteEpicPieces(db, transaction, owner, expected.Id, plan.Before, inventory, plan.Reason);
        var active = current.ActiveMainQuests;
        if (quests is not null && !plan.Reason.StartsWith("quest_", StringComparison.Ordinal))
        {
            var refreshed = DfoServer.Game.Quests.QuestInventoryProgress.Refresh(active, inventory, quests, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            WriteQuestConditions(db, transaction, current.Id, active, refreshed);
            active = refreshed.ToArray();
        }
        // 未参与本次库存操作的任务等进度取事务内实读值，不能用旧expected覆盖在线快照。
        return current with { Revision = current.Revision + 1, Inventory = inventory, ActiveMainQuests = active };
    }

    static void WriteQuestConditions(SqliteConnection db, SqliteTransaction transaction, ushort characterId,
        IReadOnlyList<ActiveMainQuest> before, IReadOnlyList<ActiveMainQuest> after)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE character_active_main_quests SET condition_state=$scalar WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$acceptance AND condition_state=$before";
        foreach (var row in after)
        {
            var old = before.Single(value => value.AcceptanceId == row.AcceptanceId);
            if (old == row) continue;
            if (old.Slot != row.Slot || old.QuestId != row.QuestId) throw new InvalidDataException("库存不能替换任务接取身份");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$slot", row.Slot); command.Parameters.AddWithValue("$quest", row.QuestId);
            command.Parameters.AddWithValue("$acceptance", row.AcceptanceId.ToString("N"));
            command.Parameters.AddWithValue("$before", (long)old.ConditionState); command.Parameters.AddWithValue("$scalar", (long)row.ConditionState);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("库存关联任务状态已变化");
        }
    }

    static bool SameInventory(InventorySnapshot left, InventoryPlan plan)
    {
        var right = plan.Before;
        if (ReferenceEquals(left, right))
            return true;
        if (left.Gold != right.Gold || left.Expansion != right.Expansion || left.ReviveCoins != right.ReviveCoins ||
            left.WinPoints != right.WinPoints || left.GoldCarryLimit != right.GoldCarryLimit || left.AuctionGoldLimit != right.AuctionGoldLimit ||
            !left.AccountWallet.Same(right.AccountWallet) || left.CargoCapacity != right.CargoCapacity || left.AccountCargo != right.AccountCargo ||
            !ShopPurchaseCount.Same(left.ShopPurchases, right.ShopPurchases) || !left.EpicPieces.Same(right.EpicPieces) ||
            left.Items.Length != right.Items.Length)
            return false;
        if (left.Items.SequenceEqual(right.Items))
            return true;
        // 顺序不同仍按完整实例比较；复用计划索引，不能让重复槽位替代缺失实例。
        var seen = new HashSet<InventorySlot>();
        return left.Items.All(item => seen.Add(item.Position) && item == plan.BeforeIndex.Find(item.Position));
    }

    static InventorySnapshot ReadInventory(SqliteConnection db, ushort characterId, SqliteTransaction? transaction = null, AccountInventory? shared = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.CommandText = "SELECT inventory_expansion,gold,revive_coins,win_points,gold_carry_limit,auction_gold_limit,account,cargo_capacity FROM characters WHERE id=$id";
        ushort expansion, capacity;
        uint gold;
        int coins, wins, carry, auction;
        string account;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidDataException("背包角色不存在");
            expansion = checked((ushort)reader.GetInt32(0));
            gold = checked((uint)reader.GetInt64(1));
            coins = reader.GetInt32(2);
            wins = reader.GetInt32(3);
            carry = reader.GetInt32(4);
            auction = reader.GetInt32(5);
            account = reader.GetString(6);
            capacity = checked((ushort)reader.GetInt32(7));
        }

        shared ??= ReadAccountInventory(db, account, transaction);
        var items = ReadInventoryItems(db, account, characterId, transaction).AddRange(shared.Items)
            .OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray();
        var purchases = ReadShopPurchases(db, account, characterId, transaction).AddRange(shared.Purchases)
            .OrderBy(row => row.NpcId).ThenBy(row => row.ItemId).ThenByDescending(row => row.AccountWide)
            .ThenBy(row => row.ResetDaily).ToImmutableArray();
        var inventory = new InventorySnapshot(items, expansion, gold)
        {
            ReviveCoins = coins, WinPoints = wins, GoldCarryLimit = carry, AuctionGoldLimit = auction,
            AccountWallet = shared.Wallet, CargoCapacity = capacity, AccountCargo = shared.Cargo,
            ShopPurchases = purchases, EpicPieces = shared.Pieces
        };
        inventory.Validate();
        return inventory;
    }

    static ImmutableArray<InventoryItem> ReadInventoryItems(SqliteConnection db, string account, ushort? characterId, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.item_uid,s.list_type,s.slot_index,i.item_core,a.detail,p.detail FROM character_inventory_items i
            LEFT JOIN character_inventory_slots s ON s.item_uid=i.item_uid AND s.account=i.account AND s.character_id IS i.character_id
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid
            WHERE i.account=$account AND i.character_id IS $id
              AND NOT EXISTS(SELECT 1 FROM mailbox_attachments m WHERE m.item_uid=i.item_uid)
            ORDER BY s.list_type,s.slot_index;
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", (object?)characterId ?? DBNull.Value);
        var items = ImmutableArray.CreateBuilder<InventoryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(ReadInventoryItem(reader));
        return items.ToImmutable();
    }

    static InventoryItem ReadInventoryItem(SqliteDataReader reader)
    {
        if (reader.IsDBNull(1))
            throw new InvalidDataException("物品缺少容器位置");
        var core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(3)) ?? throw new InvalidDataException("物品核心无法读取");
        return new(reader.GetInt64(0), new((InventoryListType)reader.GetInt32(1), checked((short)reader.GetInt32(2))), core)
        {
            Avatar = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<AvatarDetail>(reader.GetString(4)),
            Creature = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<CreatureDetail>(reader.GetString(5))
        };
    }

    static InventorySnapshot WriteInventory(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        ushort characterId, InventoryPlan plan, InventorySnapshot after, IReadOnlySet<long>? mailRetained = null,
        IReadOnlyDictionary<long, InventoryItem>? incoming = null)
    {
        var before = plan.Before;
        string reason = plan.Reason;
        var slots = plan.ChangedSlots;
        var changed = slots.ToHashSet();
        var written = new Dictionary<long, (InventoryItem Item, string Hash)>();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        foreach (var slot in slots)
        {
            BindSlot(slot);
            command.CommandText = "DELETE FROM character_inventory_slots WHERE character_id IS $id AND account=$account AND list_type=$list AND slot_index=$slot";
            command.ExecuteNonQuery();
        }

        var retained = after.Items.Where(item => item.Uid != 0).Select(item => item.Uid).ToHashSet();
        if (mailRetained is not null)
            retained.UnionWith(mailRetained);
        foreach (var item in before.Items.Where(item => !retained.Contains(item.Uid)))
        {
            BindSlot(item.Position);
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.CommandText = "DELETE FROM character_inventory_items WHERE character_id IS $id AND account=$account AND item_uid=$uid";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("物品删除失去所有权");
        }

        var updated = after.Items.ToBuilder();
        for (int i = 0; i < updated.Count; i++)
        {
            var item = updated[i];
            if (!changed.Contains(item.Position))
                continue;
            BindSlot(item.Position);
            string core = JsonSerializer.Serialize(item.Core);
            command.Parameters.AddWithValue("$core", core);
            if (item.Uid == 0)
            {
                command.CommandText = "INSERT INTO character_inventory_items(character_id,account,item_core) VALUES($id,$account,$core) RETURNING item_uid";
                item = item with { Uid = (long)command.ExecuteScalar()! };
                if (item.Avatar is not null || item.Creature is not null)
                {
                    // 详情与核心共用持久 UID，客户端宠物键为 i32；溢出使整笔发放回滚。
                    item = item with { Core = item.Core with { Value = checked((int)item.Uid) } };
                    core = JsonSerializer.Serialize(item.Core);
                    command.Parameters["$core"].Value = core;
                    command.Parameters.AddWithValue("$uid", item.Uid);
                    command.CommandText = "UPDATE character_inventory_items SET item_core=$core WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
                updated[i] = item;
            }
            else
            {
                command.Parameters.AddWithValue("$uid", item.Uid);
                var previous = plan.BeforeIndex.FindUid(item.Uid);
                var old = previous ?? incoming?.GetValueOrDefault(item.Uid) ??
                    throw new InvalidOperationException("待转移实例不属于当前快照或已验证邮件");
                command.Parameters.AddWithValue("$oldOwner", previous?.Position.List == InventoryListType.AccountCargo ? DBNull.Value : characterId);
                command.CommandText = "UPDATE character_inventory_items SET character_id=$id,item_core=$core,updated_at=CURRENT_TIMESTAMP WHERE character_id IS $oldOwner AND account=$account AND item_uid=$uid";
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("物品更新失去所有权");
                if (old.Avatar is not null && item.Avatar is null)
                {
                    command.CommandText = "DELETE FROM inventory_avatar_details WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
                if (old.Creature is not null && item.Creature is null)
                {
                    command.CommandText = "DELETE FROM inventory_creature_details WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
            }

            if (!command.Parameters.Contains("$uid"))
                command.Parameters.AddWithValue("$uid", item.Uid);
            string? avatar = item.Avatar is null ? null : JsonSerializer.Serialize(item.Avatar);
            string? creature = item.Creature is null ? null : JsonSerializer.Serialize(item.Creature);
            if (avatar is not null)
            {
                command.Parameters.AddWithValue("$detail", avatar);
                command.CommandText = "INSERT INTO inventory_avatar_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
                command.ExecuteNonQuery();
            }
            if (creature is not null)
            {
                command.Parameters.AddWithValue("$detail", creature);
                command.CommandText = "INSERT INTO inventory_creature_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
                command.ExecuteNonQuery();
            }
            command.CommandText = "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES($id,$account,$list,$slot,$uid)";
            command.ExecuteNonQuery();
            written.Add(item.Uid, (item, HashJson(core, avatar, creature)));
        }

        var result = after with { Items = updated.ToImmutable() };
        result.Validate();
        var committedIndex = new InventoryIndex(result);
        foreach (var slot in slots)
        {
            var old = plan.BeforeIndex.Find(slot);
            var current = committedIndex.Find(slot);
            BindSlot(slot);
            command.Parameters["$id"].Value = characterId; // 审计记录发起操作的角色，账号物品本身不归属该角色。
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$action", reason);
            Add("$oldUid", old?.Uid);
            Add("$newUid", current?.Uid);
            Add("$oldId", old?.Core.ItemId);
            Add("$newId", current?.Core.ItemId);
            Add("$oldValue", old?.Core.Value);
            Add("$newValue", current?.Core.Value);
            Add("$oldKind", old is null ? null : (byte)old.Core.Kind);
            Add("$newKind", current is null ? null : (byte)current.Core.Kind);
            Add("$oldCount", old?.Core.Count ?? 0);
            Add("$newCount", current?.Core.Count ?? 0);
            Add("$countDelta", (current?.Core.Count ?? 0) - (old?.Core.Count ?? 0));
            Add("$oldHash", Hash(old));
            Add("$newHash", Hash(current));
            command.CommandText = """
                INSERT INTO inventory_audit_log(account,character_id,generation,action_name,list_type,slot_index,
                  item_uid_before,item_uid_after,item_id_before,item_id_after,value_before,value_after,
                  item_kind_before,item_kind_after,count_before,count_after,count_delta,core_hash_before,core_hash_after)
                VALUES($account,$id,$generation,$action,$list,$slot,$oldUid,$newUid,$oldId,$newId,$oldValue,$newValue,
                  $oldKind,$newKind,$oldCount,$newCount,$countDelta,$oldHash,$newHash);
                """;
            command.ExecuteNonQuery();
        }

        return result;

        void BindSlot(InventorySlot slot)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", slot.List == InventoryListType.AccountCargo ? DBNull.Value : characterId);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$list", (byte)slot.List);
            command.Parameters.AddWithValue("$slot", slot.Index);
        }
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        string? Hash(InventoryItem? item)
        {
            if (item is null)
                return null;
            // 摘要不含位置；纯移动可同时复用旧值摘要，属性或 detail 变化则分别计算。
            if (written.TryGetValue(item.Uid, out var encoded) && item.Core == encoded.Item.Core &&
                item.Avatar == encoded.Item.Avatar && item.Creature == encoded.Item.Creature)
                return encoded.Hash;
            return HashJson(JsonSerializer.Serialize(item.Core), item.Avatar is null ? null : JsonSerializer.Serialize(item.Avatar),
                item.Creature is null ? null : JsonSerializer.Serialize(item.Creature));
        }
        static string HashJson(string core, string? avatar, string? creature)
        {
            // 保持既有审计 JSON 的属性顺序与 null 字段，复用实际写库的完整 JSON 片段。
            string json = avatar is null && creature is null ? core :
                "{\"Core\":" + core + ",\"Avatar\":" + (avatar ?? "null") + ",\"Creature\":" + (creature ?? "null") + "}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }
}
