using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    const string DropCollectionRule = "frozen-ground-collection-v1";

    static bool HasDropCollection(SqliteConnection db, SqliteTransaction tx, ushort id, Guid eventId)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='character_dungeon_drop_batches'";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            return false;
        command.CommandText = "SELECT count(*) FROM character_dungeon_drop_batches WHERE character_id=$id AND event_id=$event";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    static void SaveDropCollection(SqliteConnection db, SqliteTransaction tx, string account, Character expected,
        PreparedDungeonDeath prepared, FrozenDungeonGroundDeath loot)
    {
        if (!ReferenceEquals(loot.Death, prepared.Death) || loot.Identity.CharacterId != expected.Id ||
            loot.CommittedIdentity != prepared.Result.Snapshot.Identity || loot.Map != prepared.Result.Snapshot.CurrentMap ||
            expected.DungeonState is not { } state || state.LastAttempt != loot.Identity.TaskAttempt ||
            loot.Grounds.Length > 500)
            throw new InvalidDataException("完整掉落集合不属于当前角色的原死亡准备");
        var keys = new HashSet<uint>();
        for (int ordinal = 0; ordinal < loot.Grounds.Length; ordinal++)
        {
            var ground = loot.Grounds[ordinal];
            ground.Content.Validate();
            if (!ground.UsesCollectionLedger || ground.DeathEventId != prepared.Death.EventId ||
                ground.DeathIdentity != loot.Identity || ground.Reservation.Map != loot.Map ||
                ground.Reservation.MonsterKey != prepared.Death.MonsterKey || ground.ResourceIdentity != loot.ResourceIdentity ||
                ground.Reservation.Ordinal != ordinal || ground.Reservation.GroundKey == expected.Id ||
                ground.Reservation.GroundKey is 0 or > 65535 || ground.Reservation.GroundKey >= state.NextObjectKey ||
                !keys.Add(ground.Reservation.GroundKey))
                throw new InvalidDataException("完整掉落集合的序号、来源或预留key非法");
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        BindDropBatch(command, account, expected.Id, loot);
        command.CommandText = """
            INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
              map_id,monster_key,rule_id,resource_source,object_count)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$map,$monster,$rule,$source,$count)
            """;
        command.ExecuteNonQuery();
        foreach (var ground in loot.Grounds)
        {
            command.Parameters.Clear();
            BindDropObject(command, expected.Id, ground);
            command.CommandText = "SELECT count(*) FROM character_dungeon_loot_receipts WHERE character_id=$id AND ground_key=$key";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                throw new InvalidDataException("完整集合不能复用历史单件掉落的地面key");
            command.CommandText = """
                INSERT INTO character_dungeon_drop_objects(character_id,event_id,ordinal,ground_key,core,gold_amount,x,y,record_slot)
                VALUES($id,$event,$ordinal,$key,$core,$gold,$x,$y,$slot)
                """;
            command.ExecuteNonQuery();
        }
    }

    static void VerifyDropCollection(SqliteConnection db, SqliteTransaction tx, string account, ushort id,
        Guid eventId, FrozenDungeonGroundDeath loot)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        BindDropBatch(command, account, id, loot);
        if (eventId != loot.Death.EventId)
            throw new InvalidDataException("完整掉落集合重放的死亡ID变化");
        command.CommandText = """
            SELECT account,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count
            FROM character_dungeon_drop_batches WHERE character_id=$id AND event_id=$event
            """;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != account || reader.GetString(1) != loot.Identity.InstanceId.ToString("N") ||
                reader.GetInt64(2) != loot.Identity.QuestId || reader.GetInt64(3) != loot.Identity.TaskAttempt ||
                reader.GetInt64(4) != loot.Identity.Epoch || reader.GetInt64(5) != loot.Map.Id ||
                reader.GetInt64(6) != loot.Death.MonsterKey || reader.GetString(7) != DropCollectionRule ||
                reader.GetString(8) != loot.ResourceIdentity || reader.GetInt64(9) != loot.Grounds.Length)
                throw new InvalidDataException("完整掉落集合重放的来源、规则或数量变化");
        }
        command.CommandText = """
            SELECT ordinal,ground_key,core,gold_amount,x,y,record_slot
            FROM character_dungeon_drop_objects WHERE character_id=$id AND event_id=$event ORDER BY ordinal
            """;
        using var objects = command.ExecuteReader();
        foreach (var ground in loot.Grounds)
        {
            if (!objects.Read() || objects.GetInt64(0) != ground.Reservation.Ordinal ||
                objects.GetInt64(1) != ground.Reservation.GroundKey ||
                (objects.IsDBNull(2) ? null : objects.GetString(2)) != CoreJson(ground) || objects.GetInt64(3) != ground.GoldAmount ||
                objects.GetInt64(4) != ground.Reservation.X || objects.GetInt64(5) != ground.Reservation.Y ||
                objects.GetInt64(6) != ground.Reservation.RecordSlot)
                throw new InvalidDataException("完整掉落集合重放不能丢失、重排或改换任一件资产");
        }
        if (objects.Read())
            throw new InvalidDataException("完整掉落集合的持久对象数量超出冻结集合");
    }

    static string? CoreJson(DungeonGroundObject ground) => ground.Content.Item is { } core ? JsonSerializer.Serialize(core) : null;

    static void BindDropBatch(SqliteCommand command, string account, ushort id, FrozenDungeonGroundDeath loot)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$event", loot.Death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", loot.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", loot.Identity.QuestId);
        command.Parameters.AddWithValue("$attempt", loot.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", loot.Identity.Epoch);
        command.Parameters.AddWithValue("$map", loot.Map.Id);
        command.Parameters.AddWithValue("$monster", loot.Death.MonsterKey);
        command.Parameters.AddWithValue("$rule", DropCollectionRule);
        command.Parameters.AddWithValue("$source", loot.ResourceIdentity);
        command.Parameters.AddWithValue("$count", loot.Grounds.Length);
    }

    static void BindDropObject(SqliteCommand command, ushort id, DungeonGroundObject ground)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$event", ground.DeathEventId.ToString("N"));
        command.Parameters.AddWithValue("$ordinal", ground.Reservation.Ordinal);
        command.Parameters.AddWithValue("$key", ground.Reservation.GroundKey);
        command.Parameters.AddWithValue("$core", (object?)CoreJson(ground) ?? DBNull.Value);
        command.Parameters.AddWithValue("$gold", ground.GoldAmount);
        command.Parameters.AddWithValue("$x", ground.Reservation.X);
        command.Parameters.AddWithValue("$y", ground.Reservation.Y);
        command.Parameters.AddWithValue("$slot", ground.Reservation.RecordSlot);
    }

    static void VerifyStoredDropObject(SqliteConnection db, SqliteTransaction tx, string account, ushort id, DungeonGroundObject ground)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        BindDropObject(command, id, ground);
        command.CommandText = """
            SELECT b.account,b.instance_id,b.quest_id,b.task_attempt,b.epoch,b.map_id,b.monster_key,b.rule_id,b.resource_source,
              o.ordinal,o.core,o.gold_amount,o.x,o.y,o.record_slot
            FROM character_dungeon_drop_objects o JOIN character_dungeon_drop_batches b
              ON b.character_id=o.character_id AND b.event_id=o.event_id
            WHERE o.character_id=$id AND o.ground_key=$key AND o.event_id=$event
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != account || reader.GetString(1) != ground.DeathIdentity.InstanceId.ToString("N") ||
            reader.GetInt64(2) != ground.DeathIdentity.QuestId || reader.GetInt64(3) != ground.DeathIdentity.TaskAttempt ||
            reader.GetInt64(4) != ground.DeathIdentity.Epoch || reader.GetInt64(5) != ground.Reservation.Map.Id ||
            reader.GetInt64(6) != ground.Reservation.MonsterKey || reader.GetString(7) != DropCollectionRule ||
            reader.GetString(8) != ground.ResourceIdentity || reader.GetInt64(9) != ground.Reservation.Ordinal ||
            (reader.IsDBNull(10) ? null : reader.GetString(10)) != CoreJson(ground) || reader.GetInt64(11) != ground.GoldAmount ||
            reader.GetInt64(12) != ground.Reservation.X || reader.GetInt64(13) != ground.Reservation.Y ||
            reader.GetInt64(14) != ground.Reservation.RecordSlot)
            throw new InvalidDataException("拾取来源不是原完整冻结集合中的该地面资产");
    }

    DungeonPickupCommit CommitDropCollectionPickup(AccountOwners.Lease dbOwner, Character expected,
        DungeonGroundPickupPreparation prepared, InventoryPlan plan)
    {
        var ground = prepared.Ground;
        ground.Content.Validate();
        if (prepared.Identity.CharacterId != expected.Id || prepared.Identity.InstanceId != ground.DeathIdentity.InstanceId ||
            prepared.Identity.QuestId != ground.DeathIdentity.QuestId || prepared.Identity.TaskAttempt != ground.DeathIdentity.TaskAttempt)
            throw new InvalidDataException("集合拾取准备不属于本角色原实例");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, dbOwner.Account, expected.Name);
        if (current.Id != expected.Id)
            throw new InvalidOperationException("集合拾取的角色归属变化");
        VerifyStoredDropObject(db, tx, dbOwner.Account, current.Id, ground);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$key", ground.Reservation.GroundKey);
        command.CommandText = """
            SELECT account,event_id,instance_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,
              after_balance,account_revision,revision FROM character_dungeon_drop_pickups WHERE character_id=$id AND ground_key=$key
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != dbOwner.Account || reader.GetString(1) != ground.DeathEventId.ToString("N") ||
                    reader.GetString(2) != prepared.Identity.InstanceId.ToString("N") || reader.GetInt64(3) != prepared.Identity.TaskAttempt ||
                    reader.GetInt64(4) != prepared.Identity.Epoch)
                    throw new InvalidDataException("集合重复拾取的原来源变化");
                bool same = current.Revision == reader.GetInt64(12);
                if (reader.IsDBNull(8))
                {
                    var slot = new InventorySlot(InventoryListType.Main, checked((short)reader.GetInt32(5)));
                    long uid = reader.GetInt64(6);
                    var core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(7)) ?? throw new InvalidDataException("集合拾取核心损坏");
                    return new(current, true, slot, uid, same && current.Inventory.Find(slot) is { } item && item.Uid == uid && item.Core == core,
                        Amount: reader.GetInt32(9));
                }
                var kind = (CurrencyKind)reader.GetInt32(8);
                bool wallet = !CurrencyWallet.IsAccount(kind) || current.Inventory.AccountWallet.Revision == reader.GetInt64(11);
                return new(current, true, new(InventoryListType.Main, 0), 0,
                    same && wallet && CurrencyWallet.Get(current.Inventory, kind) == reader.GetInt64(10), kind, reader.GetInt32(9));
            }
        }
        ValidateInventoryPlan(expected, plan);
        CurrencyKind? currency = ground.IsGold ? CurrencyKind.Gold : CurrencyWallet.RewardKind(ground.Core.ItemId);
        int amount = ground.IsGold ? ground.GoldAmount : ground.Core.Count;
        InventorySlot destination = new(InventoryListType.Main, 0);
        if (currency is { } kindOfCurrency)
        {
            var normalized = CurrencyWallet.Set(plan.After, kindOfCurrency, CurrencyWallet.Get(plan.Before, kindOfCurrency));
            if (plan.ChangedSlots.Length != 0 || plan.ChangedCurrencies.Length != 1 || plan.ChangedCurrencies[0] != kindOfCurrency ||
                CurrencyWallet.Get(plan.After, kindOfCurrency) - CurrencyWallet.Get(plan.Before, kindOfCurrency) != amount ||
                !SameInventory(normalized, new(plan.Before, plan.Before, "currency_comparison")))
                throw new InvalidDataException("集合货币拾取必须完整保留其他资产并接收原数量");
        }
        else
        {
            if (plan.ChangedSlots.Length != 1 ||
                !SameInventory(plan.After with { Items = plan.Before.Items }, new(plan.Before, plan.Before, "item_comparison")))
                throw new InvalidDataException("集合实体拾取修改了其他资产或多个槽");
            destination = plan.ChangedSlots[0];
            var after = plan.AfterIndex.Find(destination) ?? throw new InvalidDataException("集合拾取缺少目的物品");
            var before = plan.BeforeIndex.Find(destination);
            if (destination.List != InventoryListType.Main || after.Avatar is not null || after.Creature is not null ||
                (before is null ? after.Uid != 0 || after.Core != ground.Core :
                    !ground.Core.IsStackable || after.Uid != before.Uid ||
                    after.Core != (before.Core with { Value = checked(before.Core.Value + amount) }) ||
                    (before.Core with { Value = ground.Core.Value, SortLockFlag = ground.Core.SortLockFlag }) != ground.Core))
                throw new InvalidDataException("集合实体拾取没有保留完整冻结Core及原堆叠UID");
        }
        var committed = CommitInventoryInTransaction(db, tx, dbOwner, expected, plan);
        var actual = currency is null ? committed.Inventory.Find(destination) ?? throw new InvalidOperationException("提交后缺少目的物品") : null;
        command.Parameters.AddWithValue("$account", dbOwner.Account);
        command.Parameters.AddWithValue("$event", ground.DeathEventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", prepared.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", prepared.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", prepared.Identity.Epoch);
        command.Parameters.AddWithValue("$slot", actual is null ? DBNull.Value : destination.Index);
        command.Parameters.AddWithValue("$uid", actual is null ? DBNull.Value : actual.Uid);
        command.Parameters.AddWithValue("$core", actual is null ? DBNull.Value : JsonSerializer.Serialize(actual.Core));
        command.Parameters.AddWithValue("$currency", currency is null ? DBNull.Value : (int)currency.Value);
        command.Parameters.AddWithValue("$amount", amount);
        command.Parameters.AddWithValue("$balance", currency is null ? DBNull.Value : CurrencyWallet.Get(committed.Inventory, currency.Value));
        command.Parameters.AddWithValue("$wallet", currency is null ? DBNull.Value : committed.Inventory.AccountWallet.Revision);
        command.Parameters.AddWithValue("$revision", committed.Revision);
        command.CommandText = """
            INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
              destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
            VALUES($id,$account,$event,$key,$instance,$attempt,$epoch,$slot,$uid,$core,$currency,$amount,$balance,$wallet,$revision)
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return new(committed, false, destination, actual?.Uid ?? 0, true, currency, amount);
    }
}
