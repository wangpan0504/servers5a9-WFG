using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitDungeonDiscard(AccountOwners.Lease owner, DungeonInventoryDiscardPreparation prepared) => owner.Execute(() =>
    {
        var expected = prepared.Expected;
        var plan = prepared.Plan;
        var ground = prepared.Ground;
        var identity = ground.DeathIdentity;
        var state = expected.DungeonState ?? throw new InvalidDataException("地面丢弃缺少副本owner");
        uint key = state.NextObjectKey == expected.Id ? state.NextObjectKey + 1 : state.NextObjectKey;
        var source = plan.Inventory.BeforeIndex.Find(plan.Source.Position);
        var remaining = plan.Inventory.AfterIndex.Find(plan.Source.Position);
        if (ground.InventoryOrigin != new DungeonInventoryDiscardOrigin(plan.Source, plan.Request.Count, expected.Revision) ||
            ground.IsGold || ground.Core != plan.GroundCore || ground.ResourceIdentity != "inventory" || ground.UsesCollectionLedger ||
            ground.DeathEventId == Guid.Empty || identity.CharacterId != expected.Id || identity.InstanceId == Guid.Empty ||
            identity.TaskAttempt != state.LastAttempt || identity.Epoch <= 0 || ground.Reservation.MonsterKey != expected.Id ||
            key is 0 or > ushort.MaxValue || ground.Reservation.GroundKey != key ||
            ground.Reservation.X != plan.Request.X || ground.Reservation.Y != plan.Request.Y ||
            source is null || source != plan.Source || source.Uid <= 0 ||
            plan.Request.List != (byte)source.Position.List || plan.Request.Slot != source.Position.Index ||
            plan.Request.Count == 0 || plan.Request.Count > source.Core.Count ||
            plan.Inventory.ChangedSlots.Length != 1 || plan.Inventory.ChangedSlots[0] != source.Position ||
            (plan.Request.Count == source.Core.Count ? remaining is not null :
                remaining != (source with { Core = source.Core with { Value = checked(source.Core.Value - (int)plan.Request.Count) } })) ||
            plan.GroundCore != (source.Core.IsStackable ? source.Core with { Value = checked((int)plan.Request.Count) } : source.Core))
            throw new InvalidDataException("地面丢弃的源实例、数量、完整核心或预留身份改变");
        ValidateInventoryPlan(expected, plan.Inventory);
        if (!SameInventory(plan.Inventory.After with { Items = plan.Inventory.Before.Items }, plan.Inventory))
            throw new InvalidDataException("地面丢弃包含无关资产变化");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonDiscard", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name, expected.Inventory);
        string action = InventoryGroundAction("discard", ground);
        if (current.Revision != expected.Revision && HasInventoryGroundCommit(db, tx, owner, expected.Id, action))
        {
            if (current.Id != expected.Id || current.Revision != expected.Revision + 1 ||
                current.DungeonState != (state with { NextObjectKey = key + 1 }) ||
                current.CloneTitleItemId != (plan.Inventory.CloneTitleItemIdAfter ?? expected.CloneTitleItemId) ||
                !SameInventory(current.Inventory, new(plan.Inventory.After, plan.Inventory.After, "discard-replay")))
                throw new InvalidDataException("地面丢弃恢复版本或完整结果改变");
            return current;
        }
        if (current.DungeonState != state) throw new InvalidOperationException("地面丢弃的副本owner改变");
        var inventory = new InventoryPlan(plan.Inventory.Before, plan.Inventory.After, action, plan.Inventory.CharacterPrecondition)
        {
            CloneTitleItemIdAfter = plan.Inventory.CloneTitleItemIdAfter
        };
        var committed = CommitInventoryInTransaction(db, tx, owner, current, expected, inventory);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$next", key + 1);
        command.Parameters.AddWithValue("$oldKey", state.NextObjectKey);
        command.Parameters.AddWithValue("$attempt", state.LastAttempt);
        command.CommandText = "UPDATE character_dungeon_state SET next_object_key=$next WHERE character_id=$id AND account=$account AND next_object_key=$oldKey AND last_attempt=$attempt";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("地面丢弃key预留失去原owner");
        var itemRead = ReadInventoryItemRevision(db, tx, owner.Account, committed.Id);
        tx.Commit();
        RememberInventoryItems(committed.Inventory, itemRead);
        // 地面对象只属于原Run内存；已有对象key高水位与库存仍同事务保存。
        return committed with { DungeonState = state with { NextObjectKey = key + 1 } };
    });

    static string InventoryGroundAction(string kind, DungeonGroundObject ground) =>
        $"dungeon_inventory_{kind}:{ground.DeathEventId:N}:{ground.Reservation.GroundKey}";

    // 复用已有库存审计确定结果不明的提交，不增加地面表或伪造怪物奖励回执。
    static bool HasInventoryGroundCommit(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, ushort id, string action)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$action", action);
        command.CommandText = "SELECT count(*) FROM inventory_audit_log WHERE account=$account AND character_id=$id AND generation=$generation AND action_name=$action";
        long count = Convert.ToInt64(command.ExecuteScalar());
        if (count > 1) throw new InvalidDataException("单槽地面操作出现重复库存审计");
        return count == 1;
    }

    DungeonPickupCommit CommitDiscardPickup(AccountOwners.Lease owner, Character expected, DungeonGroundPickupPreparation prepared, InventoryPlan plan)
    {
        var ground = prepared.Ground;
        var identity = prepared.Identity;
        if (ground.InventoryOrigin is null || ground.IsGold || ground.UsesCollectionLedger || identity.CharacterId != expected.Id ||
            identity.InstanceId != ground.DeathIdentity.InstanceId || identity.QuestId != ground.DeathIdentity.QuestId ||
            identity.TaskAttempt != ground.DeathIdentity.TaskAttempt || identity.Epoch < ground.DeathIdentity.Epoch ||
            expected.DungeonState?.LastAttempt != identity.TaskAttempt)
            throw new InvalidDataException("丢弃拾取准备不属于本角色原实例");
        ValidateInventoryPlan(expected, plan);
        if (plan.ChangedSlots.Length != 1 || !SameInventory(plan.After with { Items = plan.Before.Items }, plan))
            throw new InvalidDataException("丢弃拾取修改了无关资产或多个槽");
        var slot = plan.ChangedSlots[0];
        var after = plan.AfterIndex.Find(slot) ?? throw new InvalidDataException("丢弃拾取缺目标实例");
        var before = plan.BeforeIndex.Find(slot);
        if (slot.List is not (InventoryListType.Main or InventoryListType.Pet or InventoryListType.GuildMedal) ||
            after.Avatar is not null || after.Creature is not null ||
            (before is null ? after.Uid != 0 || after.Core != ground.Core :
                !ground.Core.IsStackable || after.Uid != before.Uid ||
                after.Core != (before.Core with { Value = checked(before.Core.Value + ground.Core.Count) }) ||
                (before.Core with { Value = ground.Core.Value, SortLockFlag = ground.Core.SortLockFlag }) != ground.Core))
            throw new InvalidDataException("丢弃拾取没有保留完整核心或正确数量");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDiscardPickup", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name, expected.Inventory);
        string action = InventoryGroundAction("pickup", ground);
        if (current.Revision != expected.Revision && HasInventoryGroundCommit(db, tx, owner, expected.Id, action))
        {
            var actual = current.Inventory.Find(slot) ?? throw new InvalidDataException("已提交丢弃拾取缺目标物品");
            var restored = plan.After with { Items = plan.After.Items.Replace(after, after with { Uid = actual.Uid }) };
            if (current.Id != expected.Id || current.Revision != expected.Revision + 1 || current.DungeonState != expected.DungeonState ||
                actual.Uid <= 0 || before is not null && actual.Uid != before.Uid ||
                !SameInventory(current.Inventory, new(restored, restored, "pickup-replay")))
                throw new InvalidDataException("丢弃拾取恢复版本或完整结果改变");
            return new(current, true, slot, actual.Uid, true);
        }
        var inventory = new InventoryPlan(plan.Before, plan.After, action, plan.CharacterPrecondition);
        var committed = CommitInventoryInTransaction(db, tx, owner, current, expected, inventory);
        var item = committed.Inventory.Find(slot) ?? throw new InvalidOperationException("已提交丢弃拾取缺目标物品");
        var itemRead = ReadInventoryItemRevision(db, tx, owner.Account, committed.Id);
        tx.Commit();
        RememberInventoryItems(committed.Inventory, itemRead);
        return new(committed, false, slot, item.Uid, true);
    }
}
