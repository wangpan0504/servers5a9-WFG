using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class EpicPieceShopService(EpicPieceShopCatalog rules, EpicPieceDropCatalog catalog,
    EpicPieceCatalog pieces, IReadOnlyCollection<uint> excludedDungeons)
{
    public EpicPieceShopCatalog Rules { get; } = rules;
    public bool Supports(DungeonEntryPlan entry) => entry.IsOrdinary && !entry.IsPractice && !entry.IsHellLegend &&
        !entry.IsCosmo && entry.HellParty is not null && !excludedDungeons.Contains(entry.DungeonId);

    public bool Appears(Func<int, int> draw) => Rules.AppearancePercent == 100 ||
        Rules.AppearancePercent > 0 && draw(100) < Rules.AppearancePercent;

    public EpicPieceShopPlan Plan(Character character, EpicPieceShopState state, EpicPieceShopAction action,
        int argument, Func<int, int> draw, DateTimeOffset now)
    {
        EpicPieceShopPlan Reject(string reason) => new(state, InventoryResult.Reject(19) with { RejectReason = reason });
        if (!state.Available) return Reject("shop_unavailable");
        if (state.Completed) return Reject("shop_completed");
        if (Rules.SameType != 0) return Reject("unsupported_same_type");
        int equipmentId = state.SelectedEquipment;
        if (action == EpicPieceShopAction.Select)
        {
            // CMD818传目标STK碎片编号；仅在领域内通过piece list映射到装备，报价缓存仍按唯一装备键保存。
            if (!pieces.ByPiece.TryGetValue(argument, out var piece)) return Reject("unknown_target_piece");
            equipmentId = piece.EquipmentId;
        }
        if (equipmentId == 0) return Reject("target_not_selected");
        if (!catalog.ByEquipment.TryGetValue(equipmentId, out var target)) return Reject("target_equipment_unavailable");
        if (action == EpicPieceShopAction.Select)
        {
            if (state.Offers.ContainsKey(equipmentId)) return new(state with { SelectedEquipment = equipmentId }, new(true, 0));
            var selected = Offer(target, draw);
            return selected.IsDefault ? Reject("insufficient_offer_candidates") : new(state with { SelectedEquipment = equipmentId,
                Offers = state.Offers.Add(equipmentId, selected) }, new(true, 0));
        }
        if (state.Materials.Length != Rules.Choices) return Reject("quote_unavailable");
        var before = character.Inventory;
        if (action == EpicPieceShopAction.Refresh)
        {
            if (state.Refreshes >= Rules.RefreshLimit) return Reject("refresh_limit");
            if (before.Gold < Rules.RefreshGold) return Reject("refresh_gold_required");
            var selected = Offer(target, draw);
            if (selected.IsDefault) return Reject("insufficient_offer_candidates");
            var paid = CurrencyService.Spend(before, CurrencyKind.Gold, Rules.RefreshGold);
            if (!paid.Accepted) return Reject("refresh_gold_required");
            return new(state with { Refreshes = state.Refreshes + 1, Offers = state.Offers.SetItem(equipmentId, selected) },
                new(true, 0, new(before, paid.Plan!.After, "epic_piece_shop_refresh", new(character, requirePosition: true))));
        }
        if (action != EpicPieceShopAction.Complete || argument is < 0 or > 1) return Reject("unsupported_exchange_mode");
        ItemMaterialCost[] costs = argument == 0 ? state.Materials.Select(id => new ItemMaterialCost(id, Rules.Received)).ToArray() :
            Rules.SoulCost == 0 ? [] : [new(10158124, Rules.SoulCost)];
        var spent = InventoryMaterialConsumptionService.Plan(before, costs, pieces, now);
        if (!spent.Accepted) return Reject("exchange_material_required");
        var after = spent.Plan!.After;
        after.EpicPieces.Validate(pieces);
        long balance = (long)after.EpicPieces.Get(target.Piece.Index) + Rules.Provided;
        if (balance > int.MaxValue) return Reject("target_piece_overflow");
        after = after with { EpicPieces = after.EpicPieces.Set(pieces, target.Piece.Index, (int)balance) };
        return new(state with { Completed = true },
            new(true, 0, new(before, after, "epic_piece_shop_exchange", new(character, requirePosition: true))));
    }

    ImmutableArray<int> Offer(EpicPieceDropEntry target, Func<int, int> draw)
    {
        var candidates = catalog.Entries.Where(row => row.Piece.PieceId != target.Piece.PieceId &&
            row.Equipment.MinimumLevel == target.Equipment.MinimumLevel && row.Fragment.CreationRate > 0).ToList();
        if (candidates.Count < Rules.NormalType) return default;
        var result = ImmutableArray.CreateBuilder<int>(Rules.Choices);
        Take(candidates, Rules.NormalType);
        return result.ToImmutable();

        void Take(List<EpicPieceDropEntry> source, int count)
        {
            for (int index = 0; index < count; index++)
            {
                int total = checked(source.Sum(row => row.Fragment.CreationRate));
                int value = draw(total);
                if (value < 0 || value >= total) throw new InvalidDataException("碎片交换抽样超出候选权重");
                int selected = 0;
                for (; selected < source.Count - 1 && value >= source[selected].Fragment.CreationRate; selected++)
                    value -= source[selected].Fragment.CreationRate;
                result.Add(source[selected].Piece.PieceId);
                source.RemoveAt(selected);
            }
        }
    }
}
