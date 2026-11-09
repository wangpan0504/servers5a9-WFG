using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MonsterTarotTests
{
    public static void RunCore()
    {
        const string pool = "[int data]\n7542 175000 1\n1106 300000 250\n[/int data]\n";
        string Card(string face) => "[stackable type]\n`[monster tarot card]` " + face +
            "\n[lottery use cost]\n100000\n" + pool;
        foreach (string face in new[] { "0", "9", "19", "29" })
            That(BoosterCatalog.Parse(Card(face)) is { Available: true, Lottery: true,
                AllowsLotteryGold: false, LotteryGoldCost: 100000, LotteryUseNeedItem: null },
                "塔罗牌面不冒充CMD27模式或金币奖池 " + face);
        foreach (string face in new[] { "", "-1", "+1", "0 1", "1.0", "2147483648" })
            That(!BoosterCatalog.Parse(Card(face)).Available, "塔罗牌面必须为完整非负整数 " + face);
        foreach (string invalid in new[]
        {
            Card("0").Replace("7542 175000 1", "0 175000 1"),
            Card("19") + pool,
            Card("19") + "[action type]\n[/action type]\n",
            Card("19") + "[lottery use need item]\n3314 1\n",
            Card("19").Replace("100000", "-1"),
            Card("19") + "[lottery use cost]\n100000\n"
        }) That(!BoosterCatalog.Parse(invalid).Available, "塔罗未知动作、材料、金币产物和损坏池仍拒绝");
    }

    public static void RunResources(ItemResources items, TownCatalog towns)
    {
        int checks = Count;
        const int sourceId = 10159707;
        const uint cost = 100000;
        var now = DateTimeOffset.UtcNow;
        foreach (int id in Enumerable.Range(10159697, 22))
            That(items.Boosters.Get(id) is { Available: true, Lottery: true, AllowsLotteryGold: false,
                LotteryGoldCost: cost, Pools: [{ Draws: 1 }] } definition &&
                definition.Pools[0].Rewards.All(row => row.ItemId > 0 && row.Count > 0),
                "卢克父盒引用的真实塔罗保留有限实物池及十万使用费 " + id);
        var rows = items.Boosters.Get(sourceId).Pools.Single().Rewards;
        That(rows.SequenceEqual(new BoosterReward[]
        {
            new(100170520, 175000, 1), new(108030461, 175000, 1), new(114020115, 175000, 1),
            new(10159155, 350000, 1), new(10159142, 300000, 250)
        }) && rows.Sum(row => row.Weight) == 1175000,
            "噩梦之内尔贝五行及1175000总权重沿真实PVF，不强制百万分母");
        var random = new TarotRandom();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces, random);
        var source = new InventoryItem(1, Bag(117), Core(sourceId, 2));
        var sentinel = new InventoryItem(2, Bag(66), Core(1106, 7));
        Character role = new(1, 0, "MonsterTarot", 0, towns.Room, towns.InitialReturn)
        { Level = 95, Inventory = new([source, sentinel], Gold: cost) };
        long start = 0;
        foreach (var row in rows)
        {
            foreach (long value in new[] { start, start + row.Weight - 1 })
            {
                random.Value = value;
                var product = Core(row.ItemId, row.Count);
                InventoryItem? existing = product.IsStackable ? new(3,
                    Bag(ItemSlotBoundService.Range(product.Kind, role.Inventory.Expansion).Start), Core(row.ItemId, 3)) : null;
                var actor = existing is null ? role : role with { Inventory = role.Inventory with
                    { Items = role.Inventory.Items.Add(existing) } };
                string before = JsonSerializer.Serialize(actor);
                int calls = random.Calls;
                var result = service.OpenLottery(actor, new(0, 117), out var receipt, now);
                That(result.Accepted && random.Calls == calls + 1 && random.Maximum == 1175000 &&
                    receipt is { SourceSlot: 117 } && receipt.ItemId == row.ItemId && receipt.Count == row.Count &&
                    result.Plan!.After.Gold == 0 && result.Plan.After.Find(Bag(117))!.Core == Core(sourceId, 1) &&
                    result.Plan.After.Find(Bag(66)) == sentinel &&
                    result.Plan.After.Items.Single(item => item.Core.ItemId == row.ItemId).Core ==
                        Core(row.ItemId, row.Count + (existing is null ? 0 : 3)) &&
                    (existing is null || result.Plan.After.Find(existing.Position)!.Uid == existing.Uid) &&
                    JsonSerializer.Serialize(actor) == before,
                    "真实塔罗权重两端只扣一源和十万，完整奖励按资源发放，既有堆叠UID及原快照不变");
            }
            start += row.Weight;
        }
        var poor = role with { Inventory = role.Inventory with { Gold = cost - 1 } };
        string unchanged = JsonSerializer.Serialize(poor);
        int priorCalls = random.Calls;
        var denied = service.OpenLottery(poor, new(0, 117), out var deniedReceipt, now);
        That(!denied.Accepted && denied.Plan is null && deniedReceipt is null && random.Calls == priorCalls &&
            JsonSerializer.Serialize(poor) == unchanged, "塔罗金币差一在抽样前拒绝，源、全部资产不变");
        That(!service.Open(role, new(117, 1), out _, now).Accepted &&
            !service.OpenLottery(role, new(19, 117), out _, now).Accepted && random.Calls == priorCalls,
            "塔罗仅经CMD27模式0开启，牌面19不能当请求模式，普通CMD160不能绕入");
        Console.WriteLine($"PASS {Count - checks} 卢克怪物塔罗资源及领域断言");

        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id), count, utcNow: now);
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    sealed class TarotRandom : Random
    {
        public long Value { get; set; }
        public int Calls { get; private set; }
        public long Maximum { get; private set; }
        public override long NextInt64(long maximum)
        {
            Calls++;
            Maximum = maximum;
            That(Value >= 0 && Value < maximum, "塔罗预置权重端点有效");
            return Value;
        }
    }
}
