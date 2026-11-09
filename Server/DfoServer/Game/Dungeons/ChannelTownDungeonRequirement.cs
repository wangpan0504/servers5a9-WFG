using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 频道专属城镇（如最后防线 115）选图入场的 dgn [required item]。
// 95 PVF 有近 600 个副本写了 [required item]，普通城镇的语义未核对，所以只在频道专属城镇启用。
// 超时空之战 400000016 写 `[required item] 490007844 1 1`（特殊能量隐藏装置，[daily delete item] 1）；
// 这里只核对持有（第三列为 0 也核对）；第三列非 0 的入场扣除在 GameSession.EntryCost，已泛化到任意城镇的计次范围入场（客户端 018E6EDC 47702「消耗」）。
internal static class ChannelTownDungeonRequirement
{
    public static string? Check(DungeonDefinition definition, InventorySnapshot inventory, EpicPieceCatalog? pieces, DateTimeOffset utcNow)
    {
        if (!definition.DeclaresRequiredItem)
            return null;
        if (definition.IsAncientInfinite)
        {
            var rows = definition.RequiredItemRows;
            if (rows.IsEmpty || rows.Any(row => row.Third != 1)) return "channel-town-required-item-format";
            if (pieces is null) return "channel-town-required-item-unverifiable";
            return InventoryMaterialConsumptionService.Plan(inventory,
                rows.Select(row => new ItemMaterialCost(row.ItemId, row.Count)).ToArray(), pieces, utcNow).Accepted
                ? null : "ancient-required-items-insufficient";
        }
        if (definition.RequiredItem is not { } required)
            return "channel-town-required-item-format";
        if (pieces is null)
            return "channel-town-required-item-unverifiable";
        required = DaybreakEntryCost.Select(definition.Id, required, inventory, pieces, utcNow)!;
        return InventoryMaterialConsumptionService.Plan(inventory, [new ItemMaterialCost(required.ItemId, required.Count)], pieces, utcNow).Accepted
            ? null
            : "channel-town-required-item:" + required.ItemId;
    }
}
