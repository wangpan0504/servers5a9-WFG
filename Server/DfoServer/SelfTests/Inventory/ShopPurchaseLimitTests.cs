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
            I32(13) == 1 && I32(17) == 10309042 && I32(21) == 2 && I32(25) == 0 && I32(29) == 0,
            "715新旧限购分别写两套原生缓存并保留四组完整包体");
        foreach (string declaration in new[] { "", "`account` `weekly` 0", "`account` `weekly` -1", "`account` `weekly` 2147483648",
            "`account` `weekly` 1 2", "`unknown` `weekly` 1", "`account` `yearly` 1", "`account` `weekly` 1 junk" })
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
        That(ItemShopPrice.Parse(new("[price]\n10"), false, 0).PeriodicLimit is null,
            "没有周期声明的普通商品不新增限购");
    }

    static ItemShopPrice Parse(string body) => ItemShopPrice.Parse(new("[purchase limit]\n" + body + "\n[/purchase limit]"), false, 0);
}
