using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageDungeonDailySupplyTests
{
    public static void RunCore()
    {
        int checks = Count;
        var zone = TimeZoneInfo.CreateCustomTimeZone("marriage-supply-test", TimeSpan.FromMinutes(330), "test", "test");
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var template = new ItemTemplate(10002609, ItemKind.Material, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "Ticket");
        items.PrefillTemplates([new(template.Id, template)]);
        var supply = new MarriageDungeonDailySupply(template.Id, 3);
        var relation = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 101, 1, 2, 1000) with { State = 4, RingLevel = 0 };
        foreach (int count in new[] { 0, 1, 3, 5 })
        {
            var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2030-05-02T00:29:59Z"), zone);
            var service = new ServerItemCycleService(ServerItemUsageCatalog.Empty, items,
                new("[gold limit from level]\n0 1000000\n[/gold limit from level]"),
                new("[piece list]\n90001 90002\n[/piece list]"), clock, supply);
            int day = FatigueDailyReset.Day(clock.GetUtcNow(), zone);
            var inventory = new InventorySnapshot([]) { ServerItems = ServerItemUsageState.Empty with
                { RefillDay = day, Refills = [new(template.Id, day)] } };
            short slot = checked((short)ItemSlotBoundService.Range(template.Kind, inventory).Start);
            if (count > 0) inventory = inventory with { Items = [new(42, new(InventoryListType.Main, slot), new(template.Id, template.Kind, count))] };
            var character = new Character(1, 0, "Supply", 0, new(38, 1, 450, 234), null)
                { Marriage = relation, Inventory = inventory };
            That(!service.Maintain(character, null).Plan!.Changed, "非东八区本地05:59:59不提前补票 " + count);
            clock.Advance(TimeSpan.FromSeconds(1));
            var plan = service.Maintain(character, null).Plan!;
            long Total(InventorySnapshot value) => value.Items.Where(item => item.Core.ItemId == template.Id).Sum(item => (long)item.Core.Count);
            That(Total(plan.After) == Math.Max(3, count) && plan.After.ServerItems.Refills.Single().Day == day + 1,
                "本地06:00为0级婚戒补齐到3，超过3不扣减并保存日戳 " + count);
            if (count >= 3) That(plan.After.Items.SequenceEqual(inventory.Items), "足量时不改原实例或完整核心 " + count);
            if (count > 0) That(plan.After.Items.Any(item => item.Uid == 42), "补票保留已有实例UID " + count);
            var used = character with { Inventory = plan.After with { Items = [] } };
            That(Total(service.Maintain(used, null).Plan!.After) == 0, "同日用完门票不重复发放 " + count);
            clock.Advance(TimeSpan.FromDays(1));
            That(Total(service.Maintain(used, null).Plan!.After) == 3, "次日本地06:00重新补齐 " + count);
            foreach (var noRing in new[] { character with { Marriage = null }, character with { Marriage = relation with { State = 1 } },
                character with { Id = 3 } })
                That(Total(service.Maintain(noRing, null).Plan!.After) == count, "未婚、订婚及非成员不领取婚戒补票");
        }
        Console.WriteLine($"PASS {Count - checks} 婚戒门票本地06:00补齐、超量保留及同日幂等断言");
    }
}
