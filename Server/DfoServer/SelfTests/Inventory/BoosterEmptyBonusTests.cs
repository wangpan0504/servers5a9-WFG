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

internal static class BoosterEmptyBonusTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        const int box = 490704484, bonus = 490704485;
        var now = DateTimeOffset.UtcNow;
        var missing = Open(2, new EdgeRandom(last: true));
        That(missing.Receipt.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(15, 20) }),
            "空额外奖只保留两份基础奖励，不发0号物品或金币，也不重抽黄金箱");
        var winning = Open(2, new EdgeRandom(last: false));
        That(winning.Receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(new BoosterGrantedItem[] { new(15, 10), new(bonus, 2) }),
            "额外奖命中时与基础奖一起完整发放，两个奖池各自按次数抽取");
        var bulk = Open(1000, new Random(310240));
        int basicCount = bulk.Receipt.Rewards.Single(row => row.ItemId == 15).Count;
        int bonusCount = bulk.Receipt.Rewards.Where(row => row.ItemId == bonus).Sum(row => row.Count);
        That(basicCount is >= 5000 and <= 10000 && bonusCount is > 0 and < 1000 && bulk.Receipt.Rewards.All(row => row.ItemId is 15 or bonus),
            "1000次聚合抽样同时保留额外奖和空奖，不丢池或变成必得额外奖及金币");
        var definition = resources.Boosters.Get(box);
        That(definition.Available && definition.Pools.Length == 2 && definition.Pools.All(pool => pool.Draws == 1) &&
            definition.Pools[0].Rewards.Select(row => (row.ItemId, row.Weight, row.Count)).SequenceEqual(
                new (int,long,int)[] { (15,40000,5), (15,30000,6), (15,20000,7), (15,7000,8), (15,2000,9), (15,1000,10) }) &&
            definition.Pools[1].Rewards.Select(row => (row.ItemId, row.Weight, row.Count)).SequenceEqual(
                new (int,long,int)[] { (bonus,1000,1), (0,99000,1) }),
            "当前PVF完整双池、全部数量与1000/99000额外奖权重保持原样");
        const string head = "[stackable type]\n`[booster]` 0\n[booster info]\n";
        That(!BoosterCatalog.Parse(head + "[etc]\n1 0 100 1\n[/etc]\n[/booster info]").Available &&
            !BoosterCatalog.Parse(head + "[etc]\n1 490704485 1 1 0 99 1\n[/etc]\n[/booster info]").Available,
            "没有独立基础奖池时不扩大为空整盒消费或未知空回执");
        foreach (string data in new[] { "1 -1 100 1", "1 0 100 0", "1 0 100 2" })
            That(!BoosterCatalog.Parse(head + "[etc]\n" + data + "\n[/etc]\n[/booster info]").Available,
                "负编号和非单位空奖声明仍拒绝");
        foreach (string kind in new[] { "equipment", "creature", "stackable", "cera" })
            That(!BoosterCatalog.Parse(head + "[" + kind + "]\n1 0 100 1\n[/" + kind + "]\n[/booster info]").Available,
                "未确认的其它奖励段不能借普通etc空奖放行0号");
        var range = ItemSlotBoundService.Range(ItemKind.Consumable, InventorySnapshot.Empty);
        var filled = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Concat(Enumerable.Range(ItemSlotBoundService.QuickSlotStart,
                ItemSlotBoundService.QuickSlotEnd - ItemSlotBoundService.QuickSlotStart + 1))
            .Select(slot => new InventoryItem(slot + 1, Bag((short)slot), Core(box, slot == 65 ? 2 : 1))).ToImmutableArray();
        var full = Role(2) with { Inventory = new(filled, Gold: 12345) };
        var rejected = Service(new EdgeRandom(last: true)).Open(full, new(65, 1), out var rejectedReceipt, now);
        That(!rejected.Accepted && rejected.Error == 4 && rejectedReceipt is null && rejected.Plan is null && full.Inventory.Items == filled,
            "即使额外池为空，基础奖励仍检查真实容量，失败不扣源或发奖");
        Console.WriteLine($"PASS {Count - checks} 普通礼盒空额外奖及真实双池断言");

        (InventoryResult Result, BoosterReceipt Receipt) Open(int count, Random random)
        {
            var role = Role(count);
            string before = JsonSerializer.Serialize(role.Inventory);
            var result = Service(random).Open(role, new(65, count), out var receipt, now);
            if (!result.Accepted) Console.WriteLine($"Observed moon bonus rejection: {result.Error} {result.RejectReason}");
            That(result.Accepted && receipt is not null && result.Plan is not null,
                "真实月兔礼盒基础奖和可能为空的额外池应成功规划");
            var after = result.Plan!.After;
            That(after.Gold == 12345 && after.ReviveCoins == 3 && after.WinPoints == 7 &&
                after.Items.All(item => item.Core.ItemId != box && item.Core.ItemId != 0) &&
                after.Items.Single(item => item.Core.ItemId == 1106).Core == Core(1106, 3) &&
                JsonSerializer.Serialize(role.Inventory) == before,
                "规划完整扣源并保留哨兵和原钱包，空奖不成为0号金币或修改输入快照");
            foreach (var reward in receipt!.Rewards)
                That(after.Items.Where(item => item.Core.ItemId == reward.ItemId).Sum(item => item.Core.Count) == reward.Count,
                    "完整回执与实际产物数量一致");
            return (result, receipt);
        }
        Character Role(int count) => new(1, 0, "MoonBonus", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15, Level = 1,
            Inventory = new([new(1, Bag(65), Core(box, count)), new(2, Bag(66), Core(1106, 3))], Gold: 12345)
                { ReviveCoins = 3, WinPoints = 7 }
        };
        InventoryBoosterService Service(Random random) => new(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces, random);
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(resources.Items.Get(id), count, utcNow: now);
        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    }

    sealed class EdgeRandom(bool last) : Random
    {
        public override long NextInt64(long maxValue) => last ? maxValue - 1 : 0;
    }
}
