using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class LotteryTests
{
    internal const int SourceId = 490705163;
    public static void Run(TcpTestResources resources)
    {
        int checks = Count;
        var items = resources.ItemResources;
        LegacyExpiryFixture.Restore(items.Items, Enumerable.Range(490705157, 7).Select(id => (id, "2020-03-19 06:00:00")).ToArray());
        var random = new EdgeRandom();
        var settings = new GameDebugSettings();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, settings, items.Contracts);
        var now = DateTimeOffset.UtcNow;
        var role = new Character(1, 0, "Lottery", 0, resources.Towns.Room, resources.Towns.InitialReturn) { Job = 15 };
        var definition = items.Boosters.Get(SourceId);
        That(definition.Available && definition.Lottery && definition.Pools is [{ Draws: 1 }], "真实袖珍罐为独立单次抽取入口");
        var rows = definition.Pools[0].Rewards;
        That(rows.Select(row => row.ItemId).SequenceEqual(Enumerable.Range(490705157, 6)) &&
            rows.Select(row => row.Weight).SequenceEqual(new long[] { 3240, 100, 3230, 100, 3230, 100 }) &&
            rows.All(row => row.Count == 1) && rows.Sum(row => row.Weight) == 10000, "六项奖励、数量和总权重保持PVF原表");
        long start = 0;
        foreach (var row in rows)
        {
            foreach (long value in new[] { start, start + row.Weight - 1 })
            {
                random.Value = value;
                var source = role with { Inventory = new([new(1, Bag(117), Core(SourceId, 2)), new(2, Bag(65), Core(row.ItemId, 5))]) };
                var result = service.OpenLottery(source, new(0, 117), out var receipt, now);
                That(result.Accepted && receipt is { SourceSlot: 117, Count: 1 } && receipt.ItemId == row.ItemId &&
                    receipt.RewardSlot == Bag(65) && result.Plan!.After.Find(Bag(65))!.Core.Count == 6 &&
                    result.Plan.After.Find(Bag(117))!.Core.Count == 1 && source.Inventory.Find(Bag(117))!.Core.Count == 2,
                    "每个权重区间两端均产出真实一份奖励并合并到既有槽位");
            }
            start += row.Weight;
        }
        That(start == 10000, "逐行累计与独立求和一致");
        random.Value = 0;
        var normal = role with { Inventory = new([new(1, Bag(117), Core(SourceId, 2))]) };
        foreach (var request in new[] { new LotteryOpenRequest(1, 117), new(ushort.MaxValue, 117), new(0, -1), new(0, 116) })
            That(!service.OpenLottery(normal, request, out _, now).Accepted, "未知模式与无效源不消费");
        That(!service.Open(normal, new(117, 1), out _, now).Accepted, "袖珍罐不能绕入普通礼包入口");
        That(!service.OpenLottery(role with { Inventory = new([new(1, Bag(117), Core(7542))]) }, new(0, 117), out _, now).Accepted,
            "普通礼包不能绕入袖珍罐入口");
        foreach (var core in new[] { Core(SourceId) with { EquipmentLockId = 1 }, Core(SourceId) with { ExpireTime = checked((int)(now.ToUnixTimeSeconds() - 1)) } })
            That(!service.OpenLottery(role with { Inventory = new([new(1, Bag(117), core)]) }, new(0, 117), out _, now).Accepted,
                "锁定或到期源完整保留");
        var range = ItemSlotBoundService.Range(ItemKind.Consumable, normal.Inventory.Expansion);
        var full = Enumerable.Range(range.Start, range.End - range.Start + 1)
            .Select(slot => new InventoryItem(slot, Bag((short)slot), Core(slot == 117 ? SourceId : 7542, slot == 117 ? 2 : 1))).ToArray();
        var crowded = role with { Inventory = new([.. full]) };
        That(!service.OpenLottery(crowded, new(0, 117), out _, now).Accepted && crowded.Inventory.Find(Bag(117))!.Core.Count == 2,
            "源剩余堆叠占格时满包拒绝且不扣罐子");
        crowded = role with { Inventory = new([.. full.Select(item => item.Position == Bag(117) ? item with { Core = Core(SourceId) } : item)]) };
        var freed = service.OpenLottery(crowded, new(0, 117), out var freedReceipt, now);
        That(freed.Accepted && freedReceipt!.RewardSlot == Bag(117) && freed.Plan!.After.Find(Bag(117))!.Core.ItemId == rows[0].ItemId,
            "最后一只罐子释放源格后原格接收奖励");
        settings.IgnoreCashShopAbsoluteExpiration = false;
        int calls = random.Calls;
        That(!service.OpenLottery(normal, new(0, 117), out _, now).Accepted && random.Calls == calls,
            "遵守既有期限开关并在抽取前拒绝过期候选");
        for (int length = 0; length <= 8; length++)
        {
            if (length == 4) continue;
            bool rejected = false;
            try { BoosterRequests.ReadLottery(new byte[length]); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "袖珍罐完整4B请求边界 " + length);
        }
        That(BoosterRequests.ReadLottery(Convert.FromHexString("00007500")) == new LotteryOpenRequest(0, 117), "实机请求源槽位解码");
        const string header = "[stackable type]\n`[upgradable legacy]` 1\n[int data]\n";
        foreach (string invalid in new[] { "", "1 1", "1 0 1", "1 -1 1", "0 1 1", "1 1 0", "1 1 1 2" })
            That(!BoosterCatalog.Parse(header + invalid + "\n[/int data]").Available, "无效或零权重奖池不启用 " + invalid);
        foreach (string tail in new[] { "\n[int data]\n2 1 1", "\n[/int data]" })
            That(!BoosterCatalog.Parse(header + "10 1 2\n[/int data]" + tail).Available,
                "袖珍罐不忽略额外未闭合奖池或孤立结束标记");
        var duplicate = BoosterCatalog.Parse(header + "10 1 2 10 2 3\n[/int data]");
        That(duplicate.Available && duplicate.Pools[0].Rewards.Length == 2, "重复编号的不同行保留各自权重和数量");
        Console.WriteLine($"PASS {Count - checks} 袖珍罐资源、权重与原子规划断言");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Items.Get(id), count, now, shopExpiration: true);
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    sealed class EdgeRandom : Random
    {
        public long Value { get; set; }
        public int Calls { get; private set; }
        public override long NextInt64(long maxValue) { Calls++; That(Value >= 0 && Value < maxValue, "权重端点有效"); return Value; }
    }
}
