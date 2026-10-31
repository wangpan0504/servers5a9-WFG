using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class FiniteJarTests
{
    public static void RunResources(ItemResources items, TownCatalog towns)
    {
        int checks = Count;
        const string header = "[stackable type]\n`[upgradable legacy]` 0\n[int data]\n";
        That(BoosterCatalog.Parse(header + "0 2 555 7542 3 1\n[/int data]") is
            { Available: true, Lottery: true, Pools: [{ Draws: 1, Rewards.Length: 2 }] },
            "类型0有限三列池保留金币和实物独立权重行");
        foreach (string invalid in new[] { "", "0 1", "0 0 1", "-1 1 1", "0 -1 1", "0 1 0", "0 1 1 2" })
            That(!BoosterCatalog.Parse(header + invalid + "\n[/int data]").Available, "类型0不放行损坏奖池 " + invalid);
        That(!BoosterCatalog.Parse(header.Replace("legacy]` 0", "legacy]` 2") + "7542 1 1\n[/int data]").Available,
            "未知子类型继续拒绝");
        That(!BoosterCatalog.Parse(header.Replace("legacy]` 0", "legacy]` 1") + "0 1 555\n[/int data]").Available,
            "类型1保留已确认的实物奖池边界");

        var random = new EdgeRandom();
        var service = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            random, new GameDebugSettings());
        var now = DateTimeOffset.UtcNow;
        var role = new Character(1, 0, "GoldJar", 0, towns.Room, towns.InitialReturn)
        {
            Level = 95,
            Inventory = new([new(1, Bag(117), InventoryRewardGrantService.Create(items.Items.Get(7932), 2, now))], Gold: 12345)
        };
        var definition = items.Boosters.Get(7932);
        That(definition.Available && definition.Lottery && definition.CoolTime == 1000 &&
            definition.Pools.Single().Rewards.Select(row => (row.ItemId, row.Weight, row.Count)).SequenceEqual(new[]
            {
                (0, 34682L, 5555), (0, 5780L, 55555), (0, 1156L, 555555), (0, 578L, 5555555), (0, 57804L, 555)
            }), "5周年金罐保持五行原权重、金币数与冷却");
        string before = JsonSerializer.Serialize(role);
        long start = 0;
        foreach (var row in definition.Pools.Single().Rewards)
        {
            foreach (long value in new[] { start, start + row.Weight - 1 })
            {
                random.Value = value;
                var result = service.OpenLottery(role, new(0, 117), out var receipt, now);
                That(result.Accepted && receipt is { ItemId: 0 } && receipt.Count == row.Count &&
                    result.Plan!.After.Gold == role.Inventory.Gold + row.Count &&
                    result.Plan.After.Find(Bag(117))!.Core.Count == 1 && result.Plan.After.Items.Length == 1 &&
                    JsonSerializer.Serialize(role) == before, "金币权重区间两端到账且不创建实体 " + value);
                var committedRole = role with { Inventory = result.Plan!.After };
                var packet = LotteryPackets.Opened(result with { Receipt = new(committedRole, 1, 1) }, receipt);
                That(packet.Length == 130 && packet[0] == 1 && BitConverter.ToInt16(packet, 1) == 117 &&
                    packet.AsSpan(3, 123).IndexOfAnyExcept((byte)0) == -1 && BitConverter.ToInt32(packet, 126) == row.Count,
                    "金币ACK27使用零奖励记录和末尾实际增量");
                var cooledRole = role with { Inventory = result.Plan.After };
                That(!service.OpenLottery(cooledRole, new(0, 117), out _, now).Accepted,
                    "金币罐仍消费既有会话冷却");
            }
            start += row.Weight;
        }
        random.Value = 0;
        var capped = role with { Inventory = role.Inventory with { Gold = (uint)items.GoldLimits.CarryLimit(95) } };
        var denied = service.OpenLottery(capped, new(0, 117), out var deniedReceipt, now);
        That(!denied.Accepted && denied.Error == 4 && denied.Plan is null && deniedReceipt is null &&
            capped.Inventory.Find(Bag(117))!.Core.Count == 2, "金币超过携带上限整笔拒绝，不扣源");
        var range = ItemSlotBoundService.Range(ItemKind.Consumable, role.Inventory.Expansion);
        var full = role with { Inventory = role.Inventory with
        {
            Items = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot =>
                slot == 117 ? role.Inventory.Items[0] : new InventoryItem(slot + 1, Bag((short)slot),
                    InventoryRewardGrantService.Create(items.Items.Get(7542), 1, now))).ToImmutableArray()
        } };
        var fullResult = service.OpenLottery(full, new(0, 117), out _, now);
        That(fullResult.Accepted && fullResult.Plan!.After.Items.Length == full.Inventory.Items.Length &&
            fullResult.Plan.After.Gold == full.Inventory.Gold + 5555, "金币奖励不索取背包空位");
        var itemPot = items.Boosters.Get(10002916);
        var itemRole = role with { Inventory = new([new(1, Bag(117),
            InventoryRewardGrantService.Create(items.Items.Get(10002916), 1, now))]) };
        var itemResult = service.OpenLottery(itemRole, new(0, 117), out var itemReceipt, now);
        That(itemPot.Available && itemResult.Accepted && itemReceipt is { ItemId: > 0, Count: 1 } &&
            itemReceipt.ItemId == itemPot.Pools.Single().Rewards[0].ItemId &&
            itemResult.Plan!.After.Items.Single().Core.Kind == ItemKind.Equipment && itemResult.Plan.After.Gold == 0,
            "类型0普通金主神器罐仍按原池生成完整装备，不误走金币分支");
        That(!service.Open(itemRole, new(117, 1), out _, now).Accepted, "类型0袖珍罐不能走CMD160绕过专用回包");
        Console.WriteLine($"PASS {Count - checks} 普通旧罐有限池与金币回包断言");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    sealed class EdgeRandom : Random
    {
        public long Value { get; set; }
        public override long NextInt64(long maxValue) => Value >= 0 && Value < maxValue ? Value :
            throw new InvalidOperationException("随机值超出奖池范围");
    }
}
