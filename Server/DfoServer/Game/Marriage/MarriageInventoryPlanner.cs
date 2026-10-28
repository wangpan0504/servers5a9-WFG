using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Marriage;

// 同一计划包含全部成本与产物，由关系仓储在同一个写事务中提交。
internal sealed class MarriageInventoryPlanner(ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public InventoryPlan? Plan(Character character, IReadOnlyList<MarriageItemDelta> changes,
        DateTimeOffset now, uint gold = 0)
    {
        if (gold > int.MaxValue || character.Inventory.Gold < gold) return null;
        var after = character.Inventory with { Gold = character.Inventory.Gold - gold };
        foreach (var change in changes.Where(row => row.MemberId == character.Id && row.Count < 0))
        {
            if (change.Count == int.MinValue || change.ItemId <= 0) return null;
            InventoryResult consumed;
            if (change.Slot is { } slot)
            {
                if (slot > short.MaxValue || after.Find(new(InventoryListType.Main, (short)slot)) is not { } item ||
                    item.Core.ItemId != change.ItemId || item.Core.Count < -change.Count ||
                    item.Core.IsExpired(now.ToUnixTimeSeconds())) return null;
                consumed = InventoryDeleteService.Plan(after, item.Position, item.Core.ItemId, -change.Count);
            }
            else consumed = InventoryMaterialConsumptionService.Plan(after,
                [new ItemMaterialCost(change.ItemId, -change.Count)], pieces, now);
            if (!consumed.Accepted) return null;
            after = consumed.Plan!.After;
        }
        var rewards = changes.Where(row => row.MemberId == character.Id && row.Count > 0)
            .Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray();
        if (rewards.Length != 0)
        {
            var granted = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = after },
                rewards, items, goldLimits, pieces, now);
            if (!granted.Accepted) return null;
            after = granted.Plan!.After;
        }
        return new(character.Inventory, after, "marriage", new(character));
    }
}
