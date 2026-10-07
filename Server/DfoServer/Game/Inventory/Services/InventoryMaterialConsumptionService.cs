using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryMaterialConsumptionService
{
    public static InventoryResult Plan(InventorySnapshot before, IReadOnlyList<ItemMaterialCost> costs, EpicPieceCatalog pieces,
        DateTimeOffset utcNow, int multiplier = 1)
    {
        if (multiplier <= 0)
            return InventoryResult.Reject();
        var totals = new Dictionary<int, int>();
        foreach (var cost in costs)
        {
            long count = (long)cost.Count * multiplier + totals.GetValueOrDefault(cost.ItemId);
            if (cost.ItemId <= 0 || cost.Count <= 0 || count > int.MaxValue)
                return InventoryResult.Reject();
            totals[cost.ItemId] = (int)count;
        }

        var after = before;
        Dictionary<int, List<int>>? candidates = null;
        InventoryItem?[]? planned = null;
        foreach (var (itemId, count) in totals)
        {
            if (pieces.ByPiece.TryGetValue(itemId, out var piece))
            {
                after.EpicPieces.Validate(pieces);
                int balance = after.EpicPieces.Get(piece.Index);
                if (balance < count)
                    return InventoryResult.Reject();
                after = after with { EpicPieces = after.EpicPieces.Set(pieces, piece.Index, balance - count) };
            }
            else if (CurrencyWallet.RewardKind(itemId) is { } currency)
            {
                var spent = CurrencyService.Spend(after, currency, count);
                if (!spent.Accepted)
                    return InventoryResult.Reject();
                after = spent.Plan!.After;
            }
            else
            {
                if (candidates is null)
                {
                    candidates = [];
                    for (int index = 0; index < before.Items.Length; index++)
                    {
                        var item = before.Items[index];
                        if (item.Position.List != InventoryListType.Main || !totals.ContainsKey(item.Core.ItemId) ||
                            item.Core.EquipmentLockId != 0 || item.Core.IsExpired(utcNow.ToUnixTimeSeconds()))
                            continue;
                        if (!candidates.TryGetValue(item.Core.ItemId, out var indexes))
                            candidates.Add(item.Core.ItemId, indexes = []);
                        indexes.Add(index);
                    }
                    foreach (var indexes in candidates.Values)
                        indexes.Sort((left, right) => before.Items[left].Position.Index.CompareTo(before.Items[right].Position.Index));
                    planned = before.Items.Select(item => (InventoryItem?)item).ToArray();
                }
                int remaining = count;
                if (!candidates.TryGetValue(itemId, out var available))
                    return InventoryResult.Reject();
                foreach (int index in available)
                {
                    var item = before.Items[index];
                    int consumed = Math.Min(remaining, item.Core.Count);
                    if (consumed == 0)
                        break;
                    if (!InventoryDeleteService.TryDelete(item, item.Core.Value, consumed, out var replacement))
                        return InventoryResult.Reject();
                    planned![index] = replacement;
                    remaining -= consumed;
                }
                if (remaining != 0)
                    return InventoryResult.Reject();
            }
        }
        if (planned is not null)
            after = after with { Items = planned.OfType<InventoryItem>().ToImmutableArray() };
        return new(true, 0, new(before, after, "material_consume"));
    }
}
