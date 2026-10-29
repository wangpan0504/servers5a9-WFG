using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class GuildNpcEnhancementBonusTests
{
    public static void RunCore()
    {
        int before = Count;
        That(InventoryEnhancementService.ApplyGuildSuccessBonus(5000, 5) == 5250 &&
            InventoryEnhancementService.ApplyGuildSuccessBonus(3333, 5) == 3499,
            "公会加成按基础成功率相乘并向下取整，不直接增加百分点");
        foreach (int weight in new[] { 0, 1, 5000, 9999, 10000 })
            That(InventoryEnhancementService.ApplyGuildSuccessBonus(weight, 0) == weight,
                "未购买福利时强化和锻造保留原成功权重");
        That(InventoryEnhancementService.ApplyGuildSuccessBonus(9999, 5) == 10000 &&
            InventoryEnhancementService.ApplyGuildSuccessBonus(0, int.MaxValue) == 0 &&
            InventoryEnhancementService.ApplyGuildSuccessBonus(10000, int.MaxValue) == 10000,
            "公会加成封顶且基础零概率不被提升，宽整数计算不溢出");
        foreach (var (weight, percent) in new[] { (-1, 5), (10001, 5), (5000, -1) })
            Reject<InvalidDataException>(() => InventoryEnhancementService.ApplyGuildSuccessBonus(weight, percent),
                "非法成功权重或负数公会加成拒绝");

        That(InventoryAmplifyUpgradeService.AdjustedFailureWeight(60000, 30000, 5) == 45400 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(66667, 30000, 5) == 54502,
            "增幅先计算现有便携器加成，再按公会相对倍率逐步取整");
        That(InventoryAmplifyUpgradeService.AdjustedFailureWeight(60000, 0, 5) == 58000 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(100000, int.MaxValue, int.MaxValue) == 100000 &&
            InventoryAmplifyUpgradeService.AdjustedFailureWeight(0, int.MaxValue, int.MaxValue) == 0,
            "普通增幅使用十万权重，零与满概率及双加成上限保持稳定");
        Reject<InvalidDataException>(() => InventoryAmplifyUpgradeService.AdjustedFailureWeight(60000, 0, -1),
            "增幅拒绝负数公会加成");
        EnhancementPlans();
        Console.WriteLine($"PASS {Count - before} guild NPC enhancement bonus assertions");
    }

    static void EnhancementPlans()
    {
        const int coupon = 990211, gear = 990212;
        const long now = 2_000_000_000;
        var sourceTemplate = new ItemTemplate(coupon, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "Coupon");
        var targetTemplate = new ItemTemplate(gear, ItemKind.Equipment, 12, 10, 45, 1, 20, 1, 10,
            "[free]", ushort.MaxValue, 0, "Gear");
        var source = new InventoryItem(91, new(InventoryListType.Main, 65), new(coupon, ItemKind.Consumable, 2));
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(gear, ItemKind.Equipment, 1234567)
            { Attr = 0xE9, Durability = 3, GenuineUpgrade = 4, EnchantCardId = 3175 });
        Character role = new(1, 0, "GuildBonus", 0, new(0, 0, 0, 0), null)
            { Level = 15, Inventory = new([source, target], Gold: 3456) };
        var request = new EnhancementRequest(EnhancementMode.Reinforce, target.Position, gear, source.Position.Index,
            -1, ReadOnlyMemory<byte>.Empty, -1, -1);
        var incremental = EnhancementCatalog.Parse(coupon,
            "[equipment reinforcement ticket]\n1 50 `additional` -1\n")!;
        InventoryResult Plan(EnhancementDefinition definition, int roll, int percent, out EnhancementReceipt? receipt) =>
            InventoryEnhancementService.Plan(role, request, now, definition, sourceTemplate, targetTemplate,
                () => roll, out receipt, percent);
        var baseline = Plan(incremental, 5100, 0, out var baseReceipt);
        var improved = Plan(incremental, 5100, 5, out var improvedReceipt);
        That(baseline.Accepted && baseReceipt is { Succeeded: false, OldLevel: 9, NewLevel: 9 } &&
            improved.Accepted && improvedReceipt is { Succeeded: true, OldLevel: 9, NewLevel: 10 },
            "相同抽签只有有效公会加成使逐级强化跨过成功边界");
        That(improved.Plan!.After.Find(source.Position)!.Core.Count == 1 &&
            improved.Plan.After.Find(target.Position) == target with { Core = target.Core with { Attr = 0xEA } } &&
            improved.Plan.After.Gold == role.Inventory.Gold,
            "公会强化只改变成功判定，扣券及目标完整字段保持原计划");
        var boundary = Plan(incremental, 5250, 5, out var boundaryReceipt);
        That(boundary.Accepted && boundaryReceipt is { Succeeded: false }, "公会成功边界为左闭右开");
        foreach (var definition in new[] { incremental with { Additional = false, Level = 10 }, incremental with { Level = 2 } })
            That(Plan(definition, 5100, 5, out var receipt).Accepted && receipt is { Succeeded: false },
                "固定等级券及跨级追加券不借用普通逐级强化加成");
        That(Plan(incremental with { SuccessWeight = 10000 }, 9999, 5, out var guaranteed).Accepted &&
            guaranteed is { Succeeded: true }, "原百分百追加券仍保持成功");
    }
}
