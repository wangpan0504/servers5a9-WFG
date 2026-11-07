using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 与死亡、成长及全部库存共用原事务；每件只写来源收据，不另开逐件拾取事务。
    static DungeonDirectPickupCommit SaveDirectPickupCommit(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character committed, DungeonDirectPickupPlan? plan)
    {
        if (plan is null || plan.Entries.IsEmpty) return DungeonDirectPickupCommit.Empty;
        foreach (var entry in plan.Entries)
        {
            var ground = entry.Ground;
            var prepared = new DungeonGroundPickupPreparation(plan.Loot.CommittedIdentity, ground);
            var item = entry.Currency is null && entry.EpicPieceIndex is null && !entry.AccountMaterial
                ? committed.Inventory.Find(entry.Destination) ?? throw new InvalidDataException("直入包提交缺少真实目标物品") : null;
            var piece = entry.EpicPieceIndex is { } index
                ? new EpicPiecePickup(index, ground.Core.ItemId, ground.Core.Count,
                    committed.Inventory.EpicPieces.Get(index), committed.Inventory.EpicPieces.Revision) : null;
            var material = entry.AccountMaterial
                ? new AccountMaterialPickup(ground.Core.ItemId, ground.Core.Count,
                    committed.Inventory.AccountWallet.Material(ground.Core.ItemId).Balance,
                    committed.Inventory.AccountWallet.Revision) : null;
            SaveFrozenPickup(db, tx, owner, committed, prepared, entry.Destination, item?.Uid ?? 0, item?.Core,
                entry.Currency, entry.Amount, piece, material);
        }
        return new(plan.Entries.Select(entry => entry.Ground.Reservation.GroundKey).ToImmutableArray(),
            plan.Entries.Where(entry => entry.Currency is null && entry.EpicPieceIndex is null && !entry.AccountMaterial)
                .Select(entry => entry.Destination).Distinct().OrderBy(slot => slot.List).ThenBy(slot => slot.Index).ToImmutableArray(),
            plan.Entries.Where(entry => entry.EpicPieceIndex is not null).Select(entry => entry.EpicPieceIndex!.Value)
                .Distinct().Order().ToImmutableArray(), plan.Entries.Any(entry => entry.AccountMaterial),
            plan.Entries.Where(entry => entry.Currency is not null).Select(entry => entry.Currency!.Value).Distinct().Order().ToImmutableArray());
    }

    static DungeonDirectPickupCommit ReadDirectPickupCommit(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character current, FrozenDungeonGroundDeath? loot, DungeonDirectPickupPlan? plan)
    {
        if (loot is null || plan is null) return DungeonDirectPickupCommit.Empty;
        var keys = ImmutableArray.CreateBuilder<uint>();
        var slots = new HashSet<InventorySlot>();
        var pieces = new HashSet<int>();
        var currencies = new HashSet<CurrencyKind>();
        bool materials = false;
        foreach (var ground in loot.Grounds)
        {
            var pickup = ReadFrozenPickup(db, tx, owner, current,
                new(loot.CommittedIdentity, ground), plan.Pieces, plan.Items);
            if (pickup is null) continue;
            if (!pickup.ProjectionCurrent)
                throw new InvalidDataException("死亡事务直入包回放与当前库存不一致");
            keys.Add(ground.Reservation.GroundKey);
            if (pickup.EpicPiece is { } piece) pieces.Add(piece.Index);
            else if (pickup.AccountMaterial is not null) materials = true;
            else if (pickup.Currency is { } currency) currencies.Add(currency);
            else slots.Add(pickup.Destination);
        }
        return new(keys.ToImmutable(), slots.OrderBy(slot => slot.List).ThenBy(slot => slot.Index).ToImmutableArray(),
            pieces.Order().ToImmutableArray(), materials, currencies.Order().ToImmutableArray());
    }
}
