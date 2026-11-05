using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoad2019ItemLifetimeTests
{
    public static void RunCore()
    {
        int checks = Count;
        const int id = 490013728;
        long old = new DateTimeOffset(2020, 1, 14, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var source = new ItemExpiration(old, 3, 1, 4);
        That(EpicRoad2019ItemLifetime.Restore(id, source) == source with { AbsoluteTime = ItemExpiration.MaximumUnixTime },
            "仅旧固定日期变化，相对天数和每日每周期限保留");
        That(EpicRoad2019ItemLifetime.Restore(10308559, source) == source &&
            EpicRoad2019ItemLifetime.Restore(id, source with { AbsoluteTime = old + 1 }).AbsoluteTime == old + 1,
            "不取消普通物品或其它日期");
        Console.WriteLine($"PASS {Count - checks} 星河旧删期范围断言");
    }

    public static void RunResources(ItemResources resources)
    {
        int checks = Count;
        int old = checked((int)new DateTimeOffset(2020, 1, 14, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds());
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        int[] ids = [490013716,490013717,490013718,490013719,490013720,490013721,490013722,490013723,490013724,
            490013725,490013726,490013727,490013728,490013729,490013751,490013810];
        foreach (int id in ids)
        {
            var template = resources.Items.Get(id);
            var created = InventoryRewardGrantService.Create(template, 1, now);
            That(!created.IsExpired(now.ToUnixTimeSeconds()) && created.ExpireTime != old,
                "明确活动名册的新道具不沿用旧活动固定删除日：" + id);
            var legacy = new InventoryItem(id, new(InventoryListType.Main, 60), created with { ExpireTime = old, Attr = 3 });
            var repaired = InventoryExpiryService.RepairZeroPeriod(new([legacy]), resources.Items).Plan!.After.Items.Single();
            That(repaired == legacy with { Core = legacy.Core with { ExpireTime = ItemExpiration.MaximumUnixTime } } &&
                InventoryExpiryService.Plan(new([repaired]), now.ToUnixTimeSeconds()).Plan is { Changed: false },
                "旧删期实例保留UID、数量及属性，关闭后不再因旧日期删除：" + id);
            var custom = legacy with { Core = legacy.Core with { ExpireTime = old + 1 } };
            That(InventoryExpiryService.RepairZeroPeriod(new([custom]), resources.Items).Plan!.After.Items.Single() == custom,
                "同活动道具的其它明确期限不被覆盖");
        }
        var original = resources.Items.Get(490013728);
        var prior = new InventoryItem(42, new(InventoryListType.Main, 60),
            InventoryRewardGrantService.Create(original, 1, now) with { ExpireTime = old });
        var rented = prior with { Core = prior.Core with { IsRental = true } };
        That(InventoryExpiryService.RepairZeroPeriod(new([rented]), resources.Items).Plan!.After.Items.Single() == rented,
            "租赁实例即使日期巧合也不延长");
        try
        {
            resources.Items.PrefillTemplates([KeyValuePair.Create(original.Id, original with { EpicRoad2019FixedExpirationRestored = false })]);
            That(InventoryExpiryService.RepairZeroPeriod(new([prior]), resources.Items).Plan!.After.Items.Single() == prior,
                "同ID原模板未匹配历史删除日时不修实例");
            foreach (var expiration in new[] { new ItemExpiration(), new ItemExpiration(ItemExpiration.MaximumUnixTime, Days: 3),
                new ItemExpiration(ItemExpiration.MaximumUnixTime, DailyDelete: 1),
                new ItemExpiration(ItemExpiration.MaximumUnixTime, WeeklyDeleteDay: 4) })
            {
                resources.Items.PrefillTemplates([KeyValuePair.Create(original.Id, original with { Expiration = expiration })]);
                That(InventoryExpiryService.RepairZeroPeriod(new([prior]), resources.Items).Plan!.After.Items.Single() == prior,
                    "同ID未匹配原模板或另有相对周期期限时保留原实例期限");
            }
        }
        finally { resources.Items.PrefillTemplates([KeyValuePair.Create(original.Id, original)]); }
        var ordinary = new InventoryItem(999, new(InventoryListType.Main, 60), new(10308559, ItemKind.Material, 1) { ExpireTime = old });
        That(InventoryExpiryService.RepairZeroPeriod(new([ordinary]), resources.Items).Plan!.After.Items.Single() == ordinary,
            "普通产出材料即便同截止也不取消期限");
        Console.WriteLine($"PASS {Count - checks} 星河旧活动期限定向兼容断言");
    }
}
