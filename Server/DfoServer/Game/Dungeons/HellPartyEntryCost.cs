using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 实验性（未在95客户端实证）：深渊派对入场票消耗。依据参考项目 90 轻装上阵 dungeon_hell_party_entry.go：
// - 票数 = dgn 里 [hell party required item count]（95 PVF 显式声明，黑暗都市=30）；缺失时回退 (40*minLevel-1800)/100+10（91 级=28）。
// - 候选 = 区域 [item condition] 里每个物品 id（哈林为 3330 深渊派对邀请函），逐个尝试，第一个数量够的整笔扣除，不拆分混扣。
// - 95 PVF 没有 [hell freepass item]，所以没有免票路径。
internal sealed record HellPartyTicket(int ItemId, int Count);

internal static class HellPartyEntryCost
{
    public static int RequiredCount(DungeonHellPartyRoom room, int? minimumLevel)
    {
        if (room.RequiredItemCount > 0) return room.RequiredItemCount;
        return minimumLevel is { } level and > 0 ? Math.Max(0, (40 * level - 1800) / 100 + 10) : 0;
    }

    // 选第一个背包里数量够的候选并给出提交用的物品计划；全不够 / 票数无依据（<=0） / 无候选 都视为不能入场。
    public static (HellPartyTicket? Ticket, InventoryPlan? Plan) Plan(InventorySnapshot inventory, IReadOnlyList<int> candidates, int count,
        EpicPieceCatalog pieces, DateTimeOffset utcNow)
    {
        if (count <= 0) return (null, null);
        foreach (int itemId in candidates.Where(id => id > 0).Distinct())
        {
            var spent = InventoryMaterialConsumptionService.Plan(inventory, [new ItemMaterialCost(itemId, count)], pieces, utcNow);
            if (spent.Accepted && spent.Plan is { } plan) return (new(itemId, count), plan);
        }
        return (null, null);
    }
}