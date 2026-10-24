using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void CacheDungeonLoot(AccountOwners.Lease owner, Character expected,
        PreparedDungeonDeath prepared, FrozenDungeonGroundDeath? loot)
    {
        if (loot is null) return;
        if (loot.UsesCollectionLedger)
        {
            CacheDropCollection(owner, expected, prepared, loot);
            return;
        }
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
        DungeonTransientReceipts.Get(owner, loot.Identity).StoreLoot(loot);
    }

    static void VerifyCachedDungeonLoot(AccountOwners.Lease owner, ushort characterId,
        Guid eventId, FrozenDungeonGroundDeath? loot)
    {
        var original = DungeonTransientReceipts.FindLoot(owner, characterId, eventId);
        if (loot is null)
        {
            if (original is not null) throw new InvalidDataException("已有冻结结果不能在成长重放时省略");
            return;
        }
        if (original is null || loot.Identity.CharacterId != characterId || loot.Death.EventId != eventId)
            throw new InvalidDataException("本实例的临时冻结掉落缺失，不能恢复旧副本");
        DungeonTransientReceipts.VerifyLoot(original, loot);
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
        if (ground.InventoryOrigin is not null)
            return CommitDiscardPickup(owner, expected, prepared, plan);
        if (ground.UsesCollectionLedger)
            return CommitDropCollectionPickup(owner, expected, prepared, plan);
        ValidateLootCore(ground.Core);
        if (prepared.Identity.CharacterId != expected.Id || prepared.Identity.InstanceId != ground.DeathIdentity.InstanceId ||
            prepared.Identity.TaskAttempt != ground.DeathIdentity.TaskAttempt)
            throw new InvalidDataException("拾取准备不属于本角色实例");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonPickup", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("拾取角色归属变化");
        // 存储的原掉落是资格来源，网络坐标/token不在资产判断中。
        VerifyStoredGround(db, tx, owner, current.Id, ground);
        var receipts = DungeonTransientReceipts.Find(owner, prepared.Identity)
            ?? throw new InvalidDataException("拾取需要当前实例的原临时数据");
        if (receipts.Pickups.TryGetValue(ground.Reservation.GroundKey, out var previous))
        {
            previous.Verify(prepared);
            if (previous.Committed) return previous.Replay(current, prepared);
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
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
            current.ActiveMainQuests, inventory, quests, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),DfoServer.Game.Quests.QuestEligibility.Completed(current)).ToArray();
        WriteQuestConditions(db, tx, current.Id, current.ActiveMainQuests, active);
        var actual = inventory.Find(slot) ?? throw new InvalidOperationException("已提交拾取缺目标槽");
        var character = current with { Revision = current.Revision + 1, Inventory = inventory, ActiveMainQuests = active };
        var cached = new DungeonTransientPickup(prepared, character.Revision, slot, actual.Uid, actual.Core, null, 0, 0, 0);
        receipts.Pickups[ground.Reservation.GroundKey] = cached;
        tx.Commit();
        cached.Committed = true;
        return new DungeonPickupCommit(character, false, slot, actual.Uid, true);
    });

    static void VerifyStoredGround(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, ushort id,
        DungeonGroundObject ground)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", id);
        var original = DungeonTransientReceipts.Find(owner, ground.DeathIdentity)?.Grounds.GetValueOrDefault(ground.Reservation.GroundKey);
        if (original != ground || ground.DeathIdentity.CharacterId != id)
            throw new InvalidDataException("拾取来源不是当前实例缓存中的完整地面对象");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", ground.DeathEventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", ground.DeathIdentity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", ground.DeathIdentity.TaskAttempt);
        command.CommandText = """
            SELECT 1 FROM character_dungeon_growth_receipts
            WHERE character_id=$id AND account=$account AND event_id=$event AND instance_id=$instance AND task_attempt=$attempt
            """;
        if (command.ExecuteScalar() is null) throw new InvalidDataException("掉落的原死亡资产事务尚未提交");
    }
}
