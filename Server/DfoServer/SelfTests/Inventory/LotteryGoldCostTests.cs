using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class LotteryGoldCostTests
{
    public static void RunCore()
    {
        const string header = "[stackable type]\n`[upgradable legacy]` 1\n";
        const string pool = "[int data]\n7542 1 1\n[/int data]\n";
        string Cost(string value) => header + "[lottery use cost]\n" + value + "\n" + pool;
        That(BoosterCatalog.Parse(header + pool) is { Available: true, LotteryGoldCost: 0 }, "无费用声明不收费");
        foreach (uint value in new uint[] { 0, 2000, 40000000, uint.MaxValue })
            That(BoosterCatalog.Parse(Cost(value.ToString(System.Globalization.CultureInfo.InvariantCulture))) is
                { Available: true } parsed && parsed.LotteryGoldCost == value, "保留完整非负金币使用价 " + value);
        foreach (string value in new[] { "", "-1", "+1", "1 2", "1.0", "4294967296", "`2000`" })
            That(!BoosterCatalog.Parse(Cost(value)).Available, "金币费用坏值不被默认成免费 " + value);
        foreach (string text in new[]
        {
            Cost("2000") + "[lottery use cost]\n2000\n",
            header + "[/lottery use cost]\n2000\n" + pool,
            header + "[etc]\n[lottery use cost]\n2000\n[/etc]\n" + pool,
            header + "[lottery use cost\n2000\n" + pool,
            Cost("2000").Replace("[upgradable legacy]", "[booster]"),
            Cost("2000").Replace("legacy]` 1", "legacy]` 0"),
            Cost("2000").Replace("7542 1 1", "0 1 1")
        }) That(!BoosterCatalog.Parse(text).Available, "重复、嵌套、闭合块、残缺和未闭合收费入口整罐拒绝");
        That(BoosterCatalog.Parse(header + "[name]\n`[lottery use cost] 2000`\n" + pool) is
            { Available: true, LotteryGoldCost: 0 }, "说明字符串不构成费用声明");
        That(BoosterCatalog.Parse(header + "[price]\n123\n[need material]\n3314 1\n[/need material]\n" + pool) is
            { Available: true, LotteryGoldCost: 0, LotteryUseNeedItem: null }, "商店售价与购买材料不冒充开罐费用");
        string tarot = Cost("100000").Replace("[upgradable legacy]` 1", "[monster tarot card]` 11");
        That(BoosterCatalog.Parse(tarot) is { Available: true, Lottery: true, LotteryGoldCost: 100000,
            AllowsLotteryGold: false, NpcPocketPot: false }, "塔罗牌沿独立抽奖入口收取声明费用，不取得NPC罐或金币产物资格");
        foreach (string invalid in new[] { "", "-1", "11 12", "2147483648" })
            That(!BoosterCatalog.Parse(tarot.Replace("card]` 11", "card]` " + invalid)).Available,
                "塔罗牌子类型缺失、非法或溢出保持拒绝");
        That(!BoosterCatalog.Parse(tarot.Replace("7542 1 1", "0 1 1")).Available,
            "塔罗牌子类型末位为零也不允许零号金币奖励");
    }

    public static void RunResources(ItemResources items, TownCatalog towns)
    {
        LegacyPaidPotTests.Run(towns, items);
        RunTarot(items, towns);
        int checks = Count;
        const int potId = 2749786;
        const uint cost = 2000;
        var random = new FirstRandom();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces, random);
        var now = DateTimeOffset.UtcNow;
        var definition = items.Boosters.Get(potId);
        That(definition.Available && definition.Lottery, "真实蓝色迷雾罐有限实物池已可解析");
        That(definition.LotteryGoldCost == cost, "读取真实2000金币使用费用");
        var source = new InventoryItem(1, Bag(117), InventoryRewardGrantService.Create(items.Items.Get(potId), 2, now));
        Character role = new(1, 0, "CostJar", 0, towns.Room, towns.InitialReturn)
        { Level = 95, Inventory = new([source], Gold: cost - 1) };
        string before = JsonSerializer.Serialize(role);
        var denied = service.OpenLottery(role, new(0, 117), out var deniedReceipt, now);
        That(!denied.Accepted && deniedReceipt is null && random.Calls == 0 && JsonSerializer.Serialize(role) == before,
            "实际正费用罐金币差一时在抽样前拒绝，不扣源、不规划奖励且完整资产不变");
        foreach (uint gold in new[] { cost, cost + 12345 })
        {
            var funded = role with { Inventory = role.Inventory with { Gold = gold } };
            int calls = random.Calls;
            var result = service.OpenLottery(funded, new(0, 117), out var receipt, now);
            var expected = definition.Pools.Single().Rewards[0];
            That(result.Accepted && random.Calls == calls + 1 && receipt?.ItemId == expected.ItemId &&
                result.Plan!.After.Gold == gold - cost && result.Plan.After.Find(source.Position)!.Core.Count == 1 &&
                result.Plan.After.Items.Single(item => item.Core.ItemId == expected.ItemId).Core.Kind == ItemKind.Equipment &&
                funded.Inventory.Gold == gold && funded.Inventory.Find(source.Position) == source,
                "足额真实开罐只扣一笔2000金币和一只源，保留原奖池完整装备及原快照");
        }
        const int hero = 8095;
        var heroSource = source with { Core = InventoryRewardGrantService.Create(items.Items.Get(hero), 1, now) };
        var heroRole = role with { Inventory = new([heroSource], Gold: 40000123) };
        var heroResult = service.OpenLottery(heroRole, new(0, 117), out var heroReceipt, now);
        That(heroResult.Accepted && heroReceipt?.ItemId == 27582 && heroResult.Plan!.After.Gold == 123 &&
            heroResult.Plan.After.Items.All(item => item.Core.ItemId != 3314),
            "真实英雄罐扣四千万使用金币，不索取商店购买用3314材料");
        Console.WriteLine($"PASS {Count - checks} 普通袖珍罐金币费用领域断言");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);

    static void RunTarot(ItemResources items, TownCatalog towns)
    {
        int checks = Count;
        var now = DateTimeOffset.UtcNow;
        var random = new FirstRandom();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces, random);
        foreach (int id in Enumerable.Range(10159697, 22))
        {
            var definition = items.Boosters.Get(id);
            That(definition is { Available: true, Lottery: true, LotteryGoldCost: 100000, AllowsLotteryGold: false } &&
                definition.Pools is [{ Draws: 1 }] && definition.Pools[0].Rewards.All(row => row.ItemId > 0 && row.Count > 0),
                "当前候选二十二张卢克塔罗牌保留完整实物奖池及十万金币费用 " + id);
            var source = new InventoryItem(1, Bag(117), InventoryRewardGrantService.Create(items.Items.Get(id), 2, now));
            var role = new Character(1, 0, "Tarot", 0, towns.Room, towns.InitialReturn)
                { Level = 95, Inventory = new([source], Gold: 99999) };
            int calls = random.Calls;
            var denied = service.OpenLottery(role, new(0, 117), out var deniedReceipt, now);
            That(!denied.Accepted && deniedReceipt is null && random.Calls == calls && role.Inventory.Gold == 99999,
                "塔罗牌金币不足在抽样前拒绝，不扣源或金币 " + id);
            var funded = role with { Inventory = role.Inventory with { Gold = 100123 } };
            var result = service.OpenLottery(funded, new(0, 117), out var receipt, now);
            var expected = definition.Pools[0].Rewards[0];
            That(result.Accepted && receipt?.ItemId == expected.ItemId && receipt.Count == expected.Count &&
                result.Plan!.After.Gold == 123 && result.Plan.After.Find(source.Position)!.Core.Count == 1 &&
                funded.Inventory.Gold == 100123 && funded.Inventory.Find(source.Position)!.Core.Count == 2,
                "塔罗牌按原奖池一次抽取，金币、源和完整奖励共同规划且原快照不变 " + id);
        }
        Console.WriteLine($"PASS {Count - checks} 塔罗牌全系列资源、实际费用与单次抽取断言");
    }
    sealed class FirstRandom : Random
    {
        public int Calls { get; private set; }
        public override long NextInt64(long maximum) { Calls++; return 0; }
    }
}
