using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 实验性（未在95客户端实证）：深渊派对入场票消耗。依据参考项目 90 轻装上阵 dungeon_hell_party_entry.go：
// - 票数 = dgn 里 [hell party required item count]（95 PVF 显式声明，黑暗都市=30）；缺失时回退 (40*minLevel-1800)/100+10（91 级=28）。
// - 候选 = 区域 [item condition] 里每个物品 id（哈林为 3330 深渊派对邀请函），逐个尝试，第一个数量够的整笔扣除，不拆分混扣。
// - 普通通行证优先替代邀请函，仍另收传奇灵石；超星空通行证沿独立规则替代邀请函及灵石。
internal sealed record HellPartyTicket(int ItemId, int Count);

internal static class HellPartyEntryCost
{
    // 匹配客户端027AE644/027AE64F与etc/detectionstone.etc星空深渊灵石，并兼容每日签到活动灵石；数量取当前迷宫或超星空声明。
    public const int LegendStone = 10305821;
    public const int DailyAttendanceLegendStone = 490013046;
    public const int EventLegendStone = 490013823;
    public static readonly ImmutableArray<int> LegendStones = [DailyAttendanceLegendStone, EventLegendStone, LegendStone];
    public static int RequiredCount(DungeonHellPartyRoom room, int? minimumLevel)
    {
        if (room.RequiredItemCount > 0) return room.RequiredItemCount;
        return minimumLevel is { } level and > 0 ? Math.Max(0, (40 * level - 1800) / 100 + 10) : 0;
    }

    public static (HellPartyTicket? Ticket, InventoryPlan? Plan) OrdinaryPass(InventorySnapshot inventory,
        IReadOnlyList<OrdinaryHellPassCost> passes, EpicPieceCatalog pieces, DateTimeOffset utcNow)
    {
        var candidates = inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
            item.Core.EquipmentLockId == 0 && !item.Core.IsExpired(utcNow.ToUnixTimeSeconds()))
            .OrderBy(item => item.Position.Index).Select(item => passes.FirstOrDefault(row => row.ItemId == item.Core.ItemId))
            .OfType<OrdinaryHellPassCost>().Distinct();
        foreach (var candidate in candidates)
        {
            var pass = Plan(inventory, [candidate.ItemId], candidate.Count, pieces, utcNow);
            if (pass.Plan is not null) return pass;
        }
        return (null, null);
    }

    public static (HellPartyTicket? Ticket, HellPartyTicket? Stone, InventoryPlan? Plan) CosmoPass(InventorySnapshot inventory,
        IReadOnlyList<CosmoPassCost> passes, EpicPieceCatalog pieces, DateTimeOffset utcNow)
    {
        // 按有效背包槽位选择完整可支付的费用组合；星空券还须同时具备资源声明的额外灵石。
        var candidates = inventory.Items.Where(item => item.Position.List == InventoryListType.Main &&
            item.Core.EquipmentLockId == 0 && !item.Core.IsExpired(utcNow.ToUnixTimeSeconds()))
            .OrderBy(item => item.Position.Index).Select(item => passes.FirstOrDefault(row => row.ItemId == item.Core.ItemId))
            .OfType<CosmoPassCost>().Distinct();
        foreach (var candidate in candidates)
        {
            var pass = Plan(inventory,[candidate.ItemId],candidate.Count,pieces,utcNow);
            if (pass.Plan is null) continue;
            if (candidate.AdditionalStones == 0) return (pass.Ticket,null,pass.Plan);
            var stones = Plan(pass.Plan.After,LegendStones,candidate.AdditionalStones,pieces,utcNow);
            if (stones.Plan is not null) return (pass.Ticket,stones.Ticket,stones.Plan);
        }
        return (null,null,null);
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
