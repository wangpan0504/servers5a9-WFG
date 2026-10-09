using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void SaveDungeonLoot(SqliteConnection db, SqliteTransaction tx, string account, Character expected,
        PreparedDungeonDeath prepared, FrozenDungeonGroundDeath? loot)
    {
        if (loot is null) return;
        if (!ReferenceEquals(loot.Death, prepared.Death) || loot.Identity.CharacterId != expected.Id ||
            loot.CommittedIdentity != prepared.Result.Snapshot.Identity || loot.Map != prepared.Result.Snapshot.CurrentMap ||
            prepared.Monster.MonsterId is not (1 or 2) || prepared.Monster.SpawnType != 0 ||
            expected.DungeonState is not { } state || state.LastAttempt != loot.Identity.TaskAttempt)
            throw new InvalidDataException("冻结掉落不属于本次合法普通死亡");
        if (loot.Ground is { } ground)
        {
            ValidateLootCore(ground.Core);
            if (ground.DeathEventId != prepared.Death.EventId || ground.DeathIdentity != loot.Identity ||
                ground.Reservation.Map != loot.Map || ground.Reservation.MonsterKey != prepared.Death.MonsterKey ||
                ground.Reservation.GroundKey == expected.Id || ground.Reservation.GroundKey > 65535 ||
                ground.Reservation.GroundKey >= state.NextObjectKey)
                throw new InvalidDataException("地面key或完整核心不在已预留身份内");
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        BindLoot(command, account, expected.Id, prepared.Death.EventId, loot);
        command.CommandText = """
            INSERT INTO character_dungeon_loot_receipts(character_id,account,event_id,instance_id,task_attempt,epoch,map_id,
              monster_key,rule_id,resource_source,ground_key,core,x,y,record_slot)
            VALUES($id,$account,$event,$instance,$attempt,$epoch,$map,$monster,$rule,$hash,$key,$core,$x,$y,$slot)
            """;
        command.ExecuteNonQuery();
    }

    static void VerifyDungeonLoot(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId,
        Guid eventId, FrozenDungeonGroundDeath? loot)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", characterId);
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,ground_key,core,x,y,record_slot
            FROM character_dungeon_loot_receipts WHERE character_id=$id AND event_id=$event
            """;
        using var reader = command.ExecuteReader();
        bool found = reader.Read();
        if (loot is null)
        {
            if (found) throw new InvalidDataException("已有冻结结果不能在成长重放时省略");
            return;
        }
        if (!found || reader.GetString(0) != account || reader.GetString(1) != loot.Identity.InstanceId.ToString("N") ||
            reader.GetInt64(2) != loot.Identity.TaskAttempt || reader.GetInt64(3) != loot.Identity.Epoch ||
            reader.GetInt64(4) != loot.Map.Id || reader.GetInt64(5) != loot.Death.MonsterKey ||
            reader.GetString(7) != loot.ResourceIdentity)
            throw new InvalidDataException("冻结掉落的事件、规则或归属变化");
        if (loot.Ground is not { } ground)
        {
            if (!reader.IsDBNull(8) || !reader.IsDBNull(9)) throw new InvalidDataException("未命中结果不能改为物品");
            return;
        }
        if (reader.IsDBNull(8) || reader.GetInt64(8) != ground.Reservation.GroundKey ||
            reader.GetString(9) != JsonSerializer.Serialize(ground.Core) || reader.GetInt64(10) != ground.Reservation.X ||
            reader.GetInt64(11) != ground.Reservation.Y || reader.GetInt64(12) != ground.Reservation.RecordSlot)
            throw new InvalidDataException("同事件冻结的地面身份或完整核心被改变");
    }

    static void BindLoot(SqliteCommand command, string account, ushort id, Guid eventId, FrozenDungeonGroundDeath loot)
    {
        var ground = loot.Ground;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", loot.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", loot.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", loot.Identity.Epoch);
        command.Parameters.AddWithValue("$map", loot.Map.Id);
        command.Parameters.AddWithValue("$monster", loot.Death.MonsterKey);
        command.Parameters.AddWithValue("$rule", "");
        command.Parameters.AddWithValue("$hash", loot.ResourceIdentity);
        command.Parameters.AddWithValue("$key", (object?)ground?.Reservation.GroundKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$core", ground is null ? DBNull.Value : JsonSerializer.Serialize(ground.Core));
        command.Parameters.AddWithValue("$x", (object?)ground?.Reservation.X ?? DBNull.Value);
        command.Parameters.AddWithValue("$y", (object?)ground?.Reservation.Y ?? DBNull.Value);
        command.Parameters.AddWithValue("$slot", (object?)ground?.Reservation.RecordSlot ?? DBNull.Value);
    }

    static void ValidateLootCore(ItemCore core)
    {
        core.Validate();
        if (core.ItemId is not (1000 or 1047 or 1004) || core.Kind != ItemKind.Consumable || core.Count != 1 || core.ExpireTime != 0)
            throw new InvalidDataException("本服掉落账本只保存已批准单件永久消耗品");
    }

    public DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected,
        DungeonGroundPickupPreparation prepared, InventoryPlan plan) => owner.Execute(() =>
    {
        var ground = prepared.Ground;
        ValidateLootCore(ground.Core);
        if (prepared.Identity.CharacterId != expected.Id || prepared.Identity.InstanceId != ground.DeathIdentity.InstanceId ||
            prepared.Identity.TaskAttempt != ground.DeathIdentity.TaskAttempt)
            throw new InvalidDataException("拾取准备不属于本角色实例");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("拾取角色归属变化");
        // 存储的原掉落是资格来源，网络坐标/token不在资产判断中。
        VerifyStoredGround(db, tx, owner.Account, current.Id, ground);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$event", ground.DeathEventId.ToString("N"));
        command.CommandText = """
            SELECT account,ground_key,instance_id,task_attempt,epoch,destination_list,destination_slot,item_uid,after_core,revision
            FROM character_dungeon_pickup_receipts WHERE character_id=$id AND event_id=$event
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != owner.Account || reader.GetInt64(1) != ground.Reservation.GroundKey ||
                    reader.GetString(2) != prepared.Identity.InstanceId.ToString("N") || reader.GetInt64(3) != prepared.Identity.TaskAttempt ||
                    reader.GetInt64(4) != prepared.Identity.Epoch)
                    throw new InvalidDataException("重复拾取的来源变化");
                var destination = new InventorySlot((InventoryListType)reader.GetInt32(5), checked((short)reader.GetInt32(6)));
                long uid = reader.GetInt64(7);
                var savedCore = JsonSerializer.Deserialize<ItemCore>(reader.GetString(8)) ?? throw new InvalidDataException("拾取结果核心损坏");
                bool projectionCurrent = current.Revision == reader.GetInt64(9) && current.Inventory.Find(destination) is { } item &&
                    item.Uid == uid && item.Core == savedCore;
                // 只读精确重放，调用方不再加物品；晚于原receipt的库存变化须重登恢复。
                return new DungeonPickupCommit(current, true, destination, uid, projectionCurrent);
            }
        }
        if (current.Revision != expected.Revision || !SameInventory(current.Inventory, plan) ||
            plan.CharacterPrecondition?.Matches(current) == false || plan.CharacterPrecondition?.Matches(expected) == false ||
            !SameInventory(plan.After with { Items = plan.Before.Items }, new InventoryPlan(plan.Before, plan.Before, "comparison")) ||
            plan.ChangedSlots.Length != 1)
            throw new InvalidOperationException("拾取规划的完整前提或库存范围改变");
        plan.After.Validate(allocating: true);
        var slot = plan.ChangedSlots[0];
        var after = plan.AfterIndex.Find(slot) ?? throw new InvalidDataException("拾取未形成目标物品");
        var before = plan.BeforeIndex.Find(slot);
        if (slot.List != InventoryListType.Main || after.Avatar is not null || after.Creature is not null ||
            (before is null ? after.Uid != 0 || after.Core != ground.Core :
                after.Uid != before.Uid || after.Core != (before.Core with { Value = checked(before.Core.Value + 1) }) ||
                (before.Core with { Value = 1 }) != ground.Core))
            throw new InvalidDataException("拾取规划没有保留完整核心或单件增量");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("拾取revision冲突");
        var inventory = WriteInventory(db, tx, owner, current.Id, plan, plan.After);
        var active = quests is null ? current.ActiveMainQuests : DfoServer.Game.Quests.QuestInventoryProgress.Refresh(
            current.ActiveMainQuests, inventory, quests, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToArray();
        WriteQuestConditions(db, tx, current.Id, current.ActiveMainQuests, active);
        var actual = inventory.Find(slot) ?? throw new InvalidOperationException("已提交拾取缺目标槽");
        command.Parameters.AddWithValue("$key", ground.Reservation.GroundKey);
        command.Parameters.AddWithValue("$instance", prepared.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", prepared.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", prepared.Identity.Epoch);
        command.Parameters.AddWithValue("$list", (int)slot.List);
        command.Parameters.AddWithValue("$slot", slot.Index);
        command.Parameters.AddWithValue("$uid", actual.Uid);
        command.Parameters.AddWithValue("$core", JsonSerializer.Serialize(actual.Core));
        command.Parameters.AddWithValue("$afterRevision", checked(current.Revision + 1));
        command.CommandText = """
            INSERT INTO character_dungeon_pickup_receipts(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,
              destination_list,destination_slot,item_uid,after_core,revision)
            VALUES($id,$account,$event,$key,$instance,$attempt,$epoch,$list,$slot,$uid,$core,$afterRevision)
            """;
        command.ExecuteNonQuery();
        var character = current with { Revision = current.Revision + 1, Inventory = inventory, ActiveMainQuests = active };
        tx.Commit();
        return new DungeonPickupCommit(character, false, slot, actual.Uid, true);
    });

    static void VerifyStoredGround(SqliteConnection db, SqliteTransaction tx, string account, ushort id,
        DungeonGroundObject ground)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$event", ground.DeathEventId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,ground_key,core,x,y,record_slot
            FROM character_dungeon_loot_receipts WHERE character_id=$id AND event_id=$event
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != account || reader.GetString(1) != ground.DeathIdentity.InstanceId.ToString("N") ||
            reader.GetInt64(2) != ground.DeathIdentity.TaskAttempt || reader.GetInt64(3) != ground.DeathIdentity.Epoch ||
            reader.GetInt64(4) != ground.Reservation.Map.Id || reader.GetInt64(5) != ground.Reservation.MonsterKey ||
            reader.GetString(7) != ground.ResourceIdentity ||
            reader.IsDBNull(8) || reader.GetInt64(8) != ground.Reservation.GroundKey || reader.GetString(9) != JsonSerializer.Serialize(ground.Core) ||
            reader.GetInt64(10) != ground.Reservation.X || reader.GetInt64(11) != ground.Reservation.Y ||
            reader.GetInt64(12) != ground.Reservation.RecordSlot)
            throw new InvalidDataException("拾取来源不是原已提交的完整地面物品");
    }
}
