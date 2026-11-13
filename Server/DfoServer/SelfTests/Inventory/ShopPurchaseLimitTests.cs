using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class ShopPurchaseLimitTests
{
    public static void RunCore()
    {
        PurchaseCountWireContract();
        var beforeReset = new DateTimeOffset(2026, 11, 4, 21, 59, 59, TimeSpan.Zero);
        var price = Parse("`account` `weekly` 12");
        That(price.PeriodicLimit is { AccountWide: true, Count: 12, Period: ItemPurchasePeriod.Weekly } && price.Limit is null,
            "商品账号周限购独立于旧式daily purchase limit解析");
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, price, 626, 10309042, 12, beforeReset, out var capped, out var deadline) &&
            capped.ShopPurchases is [{ NpcId: 0, AccountWide: true, Count: 12, Period: ItemPurchasePeriod.Weekly }] &&
            deadline == beforeReset.AddSeconds(1).ToUnixTimeSeconds(), "商品周限购按周四06:00冻结共享商品键与提交截止");
        That(!ShopPurchaseLimitService.TryPlan(capped, price, 624, 10309042, 1, beforeReset, out var denied, out _) && denied == capped,
            "切换商人不能绕过商品周限购且失败不改原快照");
        That(ShopPurchaseLimitService.TryPlan(capped, price, 624, 10309042, 1, beforeReset.AddSeconds(1), out var reset, out _) &&
            reset.ShopPurchases.Single().Count == 1 && reset.ShopPurchases.Single().Day == capped.ShopPurchases.Single().Day + 7,
            "周四06:00后同一商品重新计数并保留本周键");
        That(!ShopPurchaseLimitService.TryPlan(reset, price, 626, 10309042, 1, beforeReset, out _, out _),
            "时钟回退不能把新周计数覆盖成旧周");
        That(ShopPurchaseLimitService.TryPlan(capped, price, 626, 490012239, 1, beforeReset, out _, out _),
            "不同商品额度彼此独立");
        var daily = Parse("`character` `daily` 2");
        var accumulated = ItemShopPrice.Parse(new("[purchase limit]\n`account` `accumulate` 1\n[purchase minimum level]\n50"), false, 0);
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, accumulated, 1, 1001, 1, beforeReset, out var accumulatedCap, out var noDeadline) &&
            noDeadline is null && accumulatedCap.ShopPurchases.Single().Day == 0 &&
            !ShopPurchaseLimitService.TryPlan(accumulatedCap, accumulated, 2, 1001, 1, beforeReset.AddYears(1), out _, out _),
            "无结束标签的累计限购正确解析且跨年、换商人均不重置");
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, daily, 1, 1001, 2, beforeReset, out var dailyCap, out _) &&
            !dailyCap.ShopPurchases.Single().AccountWide &&
            !ShopPurchaseLimitService.TryPlan(dailyCap, daily, 1, 1001, 1, beforeReset, out _, out _) &&
            ShopPurchaseLimitService.TryPlan(dailyCap, daily, 1, 1001, 1, beforeReset.AddSeconds(1), out _, out _),
            "角色每日商品限购在06:00重置且不提升为账号资格");
        var monthly = Parse("`account` `monthly` 1");
        var monthEnd = new DateTimeOffset(2026, 10, 31, 21, 59, 59, TimeSpan.Zero);
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, monthly, 1, 1001, 1, monthEnd, out var monthlyCap, out var monthDeadline) &&
            monthDeadline == monthEnd.AddSeconds(1).ToUnixTimeSeconds() &&
            ShopPurchaseLimitService.TryPlan(monthlyCap, monthly, 1, 1001, 1, monthEnd.AddSeconds(1), out _, out _),
            "月度商品限购在下月首日06:00重置");
        var combined = price with { Limit = new(true, 2, true) };
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, combined, 626, 10309042, 2, beforeReset, out var two, out _) &&
            !ShopPurchaseLimitService.TryPlan(two, combined, 626, 10309042, 1, beforeReset, out _, out _) &&
            two.ShopPurchases.Length == 2, "同一商品的新旧限购同时生效而不互相覆盖");
        byte[] packet = ShopPackets.PurchaseCounts(two.ShopPurchases.ToArray());
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(at, 4));
        That(packet.Length == 33 && packet[0] == 1 && I32(1) == 1 && I32(5) == 10309042 && I32(9) == 2 &&
            I32(13) == 0 && I32(17) == 1 && I32(21) == 10309042 && I32(25) == 2 && I32(29) == 0,
            "715新旧购买计数经第1/3组写同一购买缓存，销售第2/4组为空");
        foreach (string declaration in new[] { "`account` `weekly` 0", "`account` `weekly` -1", "`account` `weekly` 2147483648",
            "`account` `weekly` 1 2", "`unknown` `weekly` 1", "`account` `yearly` 1", "`account` `weekly` 1 junk",
            "`account` `agitwarseason` 5", "`unknown` `agitwarseason` 5", "`character` `agitwarseason` 0",
            "`character` `agitwarseason` -1", "`character` `agitwarseason` 2147483648",
            "`character` `agitwarseason` 5 6", "`character` `agitwarseason` 5 junk" })
        {
            bool rejected = false;
            try { _ = Parse(declaration); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "畸形或未知商品限购不能降级成无限购买");
        }
        That(ItemShopPrice.Parse(new("[purchase limit]\n`account` `weekly` 1\n[/purchase limit]\n[purchase limit]\n`account` `weekly` 2\n[/purchase limit]"), false, 0)
            .PeriodicLimit is { Count: 2 }, "闭合重复声明与原生一样采用最后一条完整值");
        That(ItemShopPrice.Parse(new("[purchase limit]\n`account` `weekly` 2\n[purchase limit]\n`account` `weekly` 6"), false, 0)
            .PeriodicLimit is { Count: 6 }, "无结束标签的2到6覆盖按真实10307419声明保留末次值");
        bool malformedRejected = false;
        try { _ = ItemShopPrice.Parse(new("[purchase limit]\n`account` `weekly` 0\n[purchase limit]\n`account` `weekly` 6"), false, 0); }
        catch (InvalidDataException) { malformedRejected = true; }
        That(malformedRejected, "前一声明畸形时不能借后续完整声明忽略错误");
        foreach (string declaration in new[] { "", "  ", "`character` `agitwarseason` 5" })
        {
            var unresolved = Parse(declaration);
            That(unresolved.PeriodicLimit is null && unresolved.UnresolvedPurchaseLimits.Length == 1 &&
                unresolved.UnresolvedPurchaseLimits.Single().Trim() == declaration.Trim(),
                "已见空或角色公会战赛季声明保留原文，不伪装成支持周期或无限购买");
            That(!ShopPurchaseLimitService.TryPlan(two, unresolved with { Limit = new(true, 4, true) }, 626, 10309042, 1,
                beforeReset, out var unchanged, out var unresolvedDeadline) && ReferenceEquals(unchanged, two) && unresolvedDeadline is null,
                "存在未解释声明时先拒绝购买，不改变旧计数、原快照或提交截止");
            var repeated = ItemShopPrice.Parse(new("[purchase limit]\n" + declaration +
                "\n[/purchase limit]\n[purchase limit]\n`account` `weekly` 6\n[/purchase limit]"), false, 0);
            That(repeated.PeriodicLimit is { Count: 6, Period: ItemPurchasePeriod.Weekly } &&
                repeated.UnresolvedPurchaseLimits.Length == 1 &&
                !ShopPurchaseLimitService.TryPlan(two, repeated, 626, 10309042, 1, beforeReset, out var repeatedAfter, out var repeatedDeadline) &&
                ReferenceEquals(repeatedAfter, two) && repeatedDeadline is null,
                "后续合法声明保留末次值但不能掩盖先前未解释声明或放行购买");
        }
        var both = ItemShopPrice.Parse(new("[purchase limit]\n`account` `weekly` 6\n[/purchase limit]\n" +
            "[purchase limit]\n`character` `agitwarseason` 5\n[/purchase limit]\n[purchase limit]\n[/purchase limit]"), false, 0);
        That(both.PeriodicLimit is { Count: 6 } && both.UnresolvedPurchaseLimits.Length == 2 &&
            both.UnresolvedPurchaseLimits[0].Trim() == "`character` `agitwarseason` 5" &&
            string.IsNullOrWhiteSpace(both.UnresolvedPurchaseLimits[1]) &&
            !ShopPurchaseLimitService.TryPlan(two, both, 626, 10309042, 1, beforeReset, out var bothAfter, out var bothDeadline) &&
            ReferenceEquals(bothAfter, two) && bothDeadline is null,
            "合法声明后的赛季及空声明按原顺序共同保留，不能静默覆盖成可购买");
        That(ItemShopPrice.Parse(new("[price]\n10"), false, 0).PeriodicLimit is null,
            "没有周期声明的普通商品不新增限购");
        var legacyRepeat = ItemShopPrice.Parse(new(
            "[daily purchase limit]\n`account` 10\n[/daily purchase limit]\n[daily purchase limit]\n`account` 2\n[/daily purchase limit]\n[reset daily purchase item]"), false, 0);
        That(legacyRepeat.Limit is { AccountWide: true, Count: 2, ResetDaily: true } && legacyRepeat.UnresolvedPurchaseLimits.IsEmpty,
            "旧式daily purchase limit重复段按原生末次覆盖保留后一段上限");
        bool legacyMalformed = false;
        try
        {
            _ = ItemShopPrice.Parse(new("[daily purchase limit]\n`account` 0\n[/daily purchase limit]\n[daily purchase limit]\n`account` 2\n[/daily purchase limit]"), false, 0);
        }
        catch (InvalidDataException) { legacyMalformed = true; }
        That(legacyMalformed, "旧式daily purchase limit首段畸形时不能被后段掩盖");
        var legacyEmpty = ItemShopPrice.Parse(new("[daily purchase limit]\n[/daily purchase limit]"), false, 0);
        That(legacyEmpty.Limit is null && legacyEmpty.UnresolvedPurchaseLimits.Length == 1 &&
            !ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, legacyEmpty, 70, 490000394, 1, beforeReset, out _, out _),
            "空daily purchase limit记入未决限购并在购买端拒绝，不能退化为不限购");
        var legacyMonthly = ItemShopPrice.Parse(new("[daily purchase limit]\n`character` 1\n[/daily purchase limit]\n[reset monthly purchase item]\n1"), false, 0);
        That(legacyMonthly.Limit is null && legacyMonthly.PeriodicLimit is { AccountWide: false, Count: 1, Period: ItemPurchasePeriod.Monthly } &&
            ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, legacyMonthly, 1, 10157236, 1, monthEnd, out var legacyMonthCap, out var legacyMonthDeadline) &&
            legacyMonthDeadline == monthEnd.AddSeconds(1).ToUnixTimeSeconds() &&
            !ShopPurchaseLimitService.TryPlan(legacyMonthCap, legacyMonthly, 2, 10157236, 1, monthEnd, out _, out _) &&
            ShopPurchaseLimitService.TryPlan(legacyMonthCap, legacyMonthly, 2, 10157236, 1, monthEnd.AddSeconds(1), out _, out _),
            "reset monthly purchase item将daily purchase limit映射为月度重置限购");
        bool conflictingReset = false;
        try
        {
            _ = ItemShopPrice.Parse(new("[daily purchase limit]\n`account` 1\n[/daily purchase limit]\n[reset daily purchase item]\n[reset monthly purchase item]"), false, 0);
        }
        catch (InvalidDataException) { conflictingReset = true; }
        That(conflictingReset, "日重置与月重置标签冲突时拒绝解析");
        var sharedLegacy = new ItemShopPrice(0, 0, [], new(true, 3, true));
        That(ShopPurchaseLimitService.TryPlan(InventorySnapshot.Empty, sharedLegacy, 59, 10099865, 1, beforeReset, out var firstNpc, out _) &&
            ShopPurchaseLimitService.TryPlan(firstNpc, sharedLegacy, 45, 10099865, 2, beforeReset, out var secondNpc, out _) &&
            secondNpc.ShopPurchases.Length == 2 &&
            !ShopPurchaseLimitService.TryPlan(secondNpc, sharedLegacy, 59, 10099865, 1, beforeReset, out _, out _) &&
            !ShopPurchaseLimitService.TryPlan(secondNpc, sharedLegacy, 460, 10099865, 1, beforeReset, out _, out _) &&
            ShopPurchaseLimitService.TryPlan(secondNpc, sharedLegacy, 460, 10099865, 1, beforeReset.AddSeconds(1), out _, out _),
            "旧式限购按商人分行保存，但跨商人及攻坚商店共享同一商品额度并在06:00重置");
    }

    static void PurchaseCountWireContract()
    {
        That(ShopPackets.PurchaseCounts(null).SequenceEqual(new byte[] { 0, 17, 0 }),
            "715失败仍为原有错误应答，不伪造成功空表");
        var empty = NativePurchaseCounts(ShopPackets.PurchaseCounts([]));
        That(empty.Purchases.Count == 0 && empty.Sales.Count == 0,
            "715空成功保留四组并清空原生缓存");
        ShopPurchaseCount[] rows =
        [
            new(626, 1001, true, false, 0, 2),
            new(626, 1002, false, false, 0, 3),
            new(0, 490013019, true, false, 0, 1) { Period = ItemPurchasePeriod.Accumulate },
            new(0, 1004, false, false, 0, 5) { Period = ItemPurchasePeriod.Daily }
        ];
        var decoded = NativePurchaseCounts(ShopPackets.PurchaseCounts(rows));
        That(decoded.Purchases.Count == 4 && decoded.Purchases[1001] == 2 && decoded.Purchases[1002] == 3 &&
            decoded.Purchases.GetValueOrDefault(490013019) == 1 && decoded.Purchases.GetValueOrDefault(1004) == 5 && decoded.Sales.Count == 0,
            "715周期与旧式账号/角色购买次数均恢复购买缓存，不能投进销售缓存");
        var overlap = NativePurchaseCounts(ShopPackets.PurchaseCounts(
            [new(626, 1001, true, false, 0, 2), new(0, 1001, false, false, 0, 7) { Period = ItemPurchasePeriod.Accumulate }]));
        That(overlap.Purchases is { Count: 1 } && overlap.Purchases[1001] == 7 && overlap.Sales.Count == 0,
            "715第1/3组同item按原生设值覆盖，不叠加账号/角色或新旧计数");
    }

    // 02350220的四组消费：1/3调用01FD4740，2/4调用01FD4760；两者都是按item设值。
    internal static (Dictionary<int, int> Purchases, Dictionary<int, int> Sales) NativePurchaseCounts(byte[] body)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        That(reader.ReadByte() == 1, "原生计数夹具只解成功包");
        var purchases = new Dictionary<int, int>();
        var sales = new Dictionary<int, int>();
        for (int group = 0; group < 4; group++)
        {
            int count = reader.ReadInt32();
            for (int row = 0; row < count; row++)
            {
                int item = reader.ReadInt32(), purchased = reader.ReadInt32();
                (group % 2 == 0 ? purchases : sales)[item] = purchased;
            }
        }
        That(reader.BaseStream.Position == body.Length, "715四组消费到包尾");
        return (purchases, sales);
    }

    static ItemShopPrice Parse(string body) => ItemShopPrice.Parse(new("[purchase limit]\n" + body + "\n[/purchase limit]"), false, 0);
}
