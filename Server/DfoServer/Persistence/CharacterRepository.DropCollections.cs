using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static void CacheDropCollection(AccountOwners.Lease owner, Character expected,
        PreparedDungeonDeath prepared, FrozenDungeonGroundDeath loot, bool cache = true)
    {
        if (!ReferenceEquals(loot.Death, prepared.Death) || loot.Identity.CharacterId != expected.Id ||
            loot.CommittedIdentity != prepared.Result.Snapshot.Identity || loot.Map != prepared.Result.Snapshot.CurrentMap ||
            expected.DungeonState is not { } state || state.LastAttempt != loot.Identity.TaskAttempt || loot.Grounds.Length > 500)
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
        if (cache) DungeonTransientReceipts.Get(owner, loot.Identity).StoreLoot(loot);
    }

    DungeonPickupCommit CommitDropCollectionPickup(AccountOwners.Lease dbOwner, Character expected,
        DungeonGroundPickupPreparation prepared, InventoryPlan plan, EpicPieceCatalog? pieces)
    {
        var ground = prepared.Ground;
        ground.Content.Validate();
        if (prepared.Identity.CharacterId != expected.Id || prepared.Identity.InstanceId != ground.DeathIdentity.InstanceId ||
            prepared.Identity.QuestId != ground.DeathIdentity.QuestId || prepared.Identity.TaskAttempt != ground.DeathIdentity.TaskAttempt)
            throw new InvalidDataException("集合拾取准备不属于本角色原实例");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDropCollectionPickup", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, dbOwner.Account, expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("集合拾取的角色归属变化");
        bool passive = ground.PassiveOrigin is not null;
        if (passive)
        {
            if (ground.InventoryOrigin is not null || current.DungeonState is not { } state ||
                state.LastAttempt != prepared.Identity.TaskAttempt || ground.Reservation.GroundKey is 0 or > ushort.MaxValue ||
                ground.Reservation.GroundKey >= state.NextObjectKey)
                throw new InvalidDataException("特殊物品拾取不属于当前已预留实例");
        }
        else
        {
            VerifyStoredGround(db, tx, dbOwner, current.Id, ground);
            if (ReadFrozenPickup(db, tx, dbOwner, current, prepared, pieces) is { } stored) return stored;
        }
        var receipts = DungeonTransientReceipts.Get(dbOwner, prepared.Identity);
        if (receipts.Pickups.TryGetValue(ground.Reservation.GroundKey, out var previous))
        {
            previous.Verify(prepared);
            if (previous.Committed) return previous.Replay(current, prepared);
        }
        ValidateInventoryPlan(expected, plan);
        CurrencyKind? currency = ground.IsGold ? CurrencyKind.Gold : CurrencyWallet.RewardKind(ground.Core.ItemId);
        var piece = !ground.IsGold ? pieces?.ByPiece.GetValueOrDefault(ground.Core.ItemId) : null;
        int amount = ground.IsGold ? ground.GoldAmount : ground.Core.Count;
        InventorySlot destination = new(InventoryListType.Main, 0);
        if (piece is not null)
        {
            long balance = (long)plan.Before.EpicPieces.Get(piece.Index) + amount;
            if (ground.Core.Kind != ItemKind.Material || balance > int.MaxValue || plan.ChangedSlots.Length != 0 ||
                !plan.ChangedEpicPieces.SequenceEqual(new[] { piece.Index }) ||
                !plan.After.EpicPieces.Same(plan.Before.EpicPieces.Set(pieces!, piece.Index, (int)balance)) ||
                !SameInventory(plan.After with { EpicPieces = plan.Before.EpicPieces }, new(plan.Before, plan.Before, "epic_piece_comparison")))
                throw new InvalidDataException("碎片拾取必须只增加原冻结物品对应的账号图鉴数量");
        }
        else if (currency is { } kindOfCurrency)
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
            if (!ItemSlotBoundService.Allows(plan.After, destination, ground.Core.Kind) ||
                destination != PickupSlot(plan.After, ground.Core, destination.Index) || after.Avatar is not null || after.Creature is not null ||
                (before is null ? after.Uid != 0 || after.Core != ground.Core :
                    !ground.Core.IsStackable || after.Uid != before.Uid ||
                    after.Core != (before.Core with { Value = checked(before.Core.Value + amount) }) ||
                    (before.Core with { Value = ground.Core.Value, SortLockFlag = ground.Core.SortLockFlag }) != ground.Core))
                throw new InvalidDataException("集合实体拾取没有保留完整冻结Core及原堆叠UID");
        }
        var committed = CommitInventoryInTransaction(db, tx, dbOwner, expected, plan);
        var actual = currency is null && piece is null ? committed.Inventory.Find(destination)
            ?? throw new InvalidOperationException("提交后缺少目的物品") : null;
        var epicPiece = piece is null ? null : new EpicPiecePickup(piece.Index, piece.PieceId, amount,
            committed.Inventory.EpicPieces.Get(piece.Index), committed.Inventory.EpicPieces.Revision);
        var cached = new DungeonTransientPickup(prepared, committed.Revision, destination, actual?.Uid ?? 0, actual?.Core,
            currency, amount, currency is { } kind ? CurrencyWallet.Get(committed.Inventory, kind) : 0,
            committed.Inventory.AccountWallet.Revision, epicPiece);
        if (!passive) SaveFrozenPickup(db, tx, dbOwner, committed, prepared, destination, actual?.Uid ?? 0, actual?.Core, currency, amount, epicPiece);
        tx.Commit();
        receipts.Pickups[ground.Reservation.GroundKey] = cached;
        cached.Committed = true;
        return new(committed, false, destination, actual?.Uid ?? 0, true, currency, amount) { EpicPiece = epicPiece };
    }

    static InventorySlot PickupSlot(InventorySnapshot inventory, ItemCore core, short index)
    {
        var slot = new InventorySlot(ItemSlotBoundService.List(core.Kind), index);
        return slot.List == InventoryListType.Main || ItemSlotBoundService.Allows(inventory, slot, core.Kind)
            ? slot : new(InventoryListType.Main, index);
    }
}
