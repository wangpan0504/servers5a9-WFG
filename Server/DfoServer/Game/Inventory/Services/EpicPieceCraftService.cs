using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class EpicPieceCraftService(ItemCatalog catalog, EpicPieceCatalog pieces, GoldLimitCatalog goldLimits)
{
    public InventoryResult Craft(Character character, int equipmentId, DateTimeOffset? utcNow = null)
    {
        if (!pieces.Recipes.TryGetValue(equipmentId, out var recipe))
            return InventoryResult.Reject(4);
        var before = character.Inventory;
        var now = utcNow ?? DateTimeOffset.UtcNow;
        before.EpicPieces.Validate(pieces);
        var spent = InventoryMaterialConsumptionService.Plan(before, recipe.Costs, pieces, now);
        if (!spent.Accepted)
            return InventoryResult.Reject(4);
        // 先规划消耗再发放，允许利用消耗后腾出的格子；不额外限制职业、等级或预留空格数量。
        var reward = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = spent.Plan!.After }, [new(equipmentId, 1)], catalog, goldLimits, pieces, now);
        return reward.Accepted ? new(true, 0, new(before, reward.Plan!.After, "epic_book_craft")) : InventoryResult.Reject(4);
    }
}
