using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class LegacyPaidPotTests
{
    public static void Run(TownCatalog towns, ItemResources items)
    {
        int checks = Count;
        const int pot = 490009330, reward = 490009184;
        const uint cost = 100000;
        var now = DateTimeOffset.UtcNow;
        var definition = items.Boosters.Get(pot);
        That(definition is { Available: true, Lottery: true, AllowsLotteryGold: false, RuneBox: null, LotteryGoldCost: cost } &&
            definition.Pools is [{ Draws: 1, Rewards: [{ ItemId: reward, Count: 1, Weight: 100000 }] }],
            "吃金币的袖珍罐保留真实普通legacy1实物池和十万金币费用");
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces);
        for (byte job = 0; job < 16; job++)
        foreach (uint gold in new uint[] { cost, cost + 12345 })
        {
            var actor = Role(job, gold);
            string before = JsonSerializer.Serialize(actor.Inventory);
            var result = service.OpenLottery(actor, new(0, 117), out var receipt, now);
            That(result.Accepted && receipt is { ItemId: reward, Count: 1 } && result.Plan!.After.Gold == gold - cost &&
                result.Plan.After.Find(Bag(117))!.Core == Core(pot, 1) &&
                result.Plan.After.Find(Bag(66)) == actor.Inventory.Find(Bag(66)) &&
                result.Plan.After.Items.Single(item => item.Core.ItemId == reward).Core == Core(reward, 1) &&
                JsonSerializer.Serialize(actor.Inventory) == before,
                "全部职业精确扣一源和十万金币，只发完整欧气子盒，不代开或修改原快照 " + job);
        }
        foreach (uint gold in new uint[] { 0, cost - 1 })
        {
            var actor = Role(0, gold);
            string before = JsonSerializer.Serialize(actor.Inventory);
            var denied = service.OpenLottery(actor, new(0, 117), out var receipt, now);
            That(!denied.Accepted && denied.Plan is null && receipt is null && JsonSerializer.Serialize(actor.Inventory) == before,
                "金币不足拒绝且源、哨兵和钱包不变");
        }
        var regular = Role(0, cost);
        That(!service.Open(regular, new(117, 1), out _, now).Accepted,
            "普通legacy袖珍罐不借CMD160绕过专用开罐入口");
        var expired = regular with { Inventory = regular.Inventory with
            { Items = regular.Inventory.Items.SetItem(0, regular.Inventory.Items[0] with { Core = Core(pot, 2) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) - 1 } }) } };
        That(!service.OpenLottery(expired, new(0, 117), out _, now).Accepted,
            "过期普通legacy源仍拒绝");
        var range = ItemSlotBoundService.Range(ItemKind.Consumable, regular.Inventory.Expansion);
        var full = regular with { Inventory = regular.Inventory with { Items = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Concat(Enumerable.Range(ItemSlotBoundService.QuickSlotStart, ItemSlotBoundService.QuickSlotEnd - ItemSlotBoundService.QuickSlotStart + 1))
            .Select(slot => new InventoryItem(slot + 1, Bag((short)slot), slot == 117 ? Core(pot, 2) : Core(1106, 1))).ToImmutableArray() } };
        var noRoom = service.OpenLottery(full, new(0, 117), out var noRoomReceipt, now);
        That(!noRoom.Accepted && noRoom.Error == 4 && noRoom.Plan is null && noRoomReceipt is null && full.Inventory.Gold == cost,
            "源仍占格时满消耗品页及快捷栏整笔拒绝，不扣金币或源");
        const string header = "[stackable type]\n`[legacy]` 1\n[lottery use cost]\n100000\n";
        const string pool = "[int data]\n490009184 100000 1\n[/int data]\n";
        That(BoosterCatalog.Parse(header + pool) is { Available: true, Lottery: true, LotteryGoldCost: cost, AllowsLotteryGold: false },
            "普通legacy1有限实物池走既有开罐合同");
        foreach (string invalid in new[]
        {
            header.Replace("legacy]` 1", "legacy]` 0") + pool,
            header.Replace("legacy]` 1", "legacy]` 2") + pool,
            header + pool.Replace("490009184", "0"),
            header + pool + pool,
            header + "[action type]\n`[increase chance lottery]` 1 15 0\n[/action type]\n" + pool,
            header + "[action type]\n`[rune random box]` 0 0\n[/action type]\n" + pool,
            header + "[action type]\n[/action type]\n" + pool,
            header.Replace("100000", "-1") + pool
        }) That(!BoosterCatalog.Parse(invalid).Available, "未知普通legacy子类型/动作、金币产物及损坏声明不扩大放行");
        Console.WriteLine($"PASS {Count - checks} 普通legacy实物收费袖珍罐断言");

        Character Role(byte job, uint gold) => new(1, 0, "LegacyPot", 0, towns.Room, towns.InitialReturn)
        { Job = job, Level = 95, Inventory = new([new(1, Bag(117), Core(pot, 2)), new(2, Bag(66), Core(1106, 7))], Gold: gold) };
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id), count, utcNow: now);
        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    }
}
