using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using DfoServer.Game.Rental;
using DfoServer.Network.Builders.Inventory;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using System.Collections.Immutable;

namespace DfoServer;

internal static class RentalTests
{
    public static void Run()
    {
        byte[] charge = new byte[21];
        BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(17), 41);
        var chargeRequest = RentalRequests.Charge(charge);
        That(chargeRequest.Type == 1 && chargeRequest.Count == 41, "C2S1002 21字节请求解析偏移");
        Reject(() => RentalRequests.Charge(charge[..20]), "C2S1002 非法长度拒绝");

        byte[] rent = Convert.FromHexString("5CD9C67B80207A3A401833380067C6E61700000700");
        var rentRequest = RentalRequests.Equipment(rent);
        That(rentRequest.ItemId == 401000039 && rentRequest.Job == 0 && rentRequest.Tier == 7, "C2S1001 itemId/job/tier解析");

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var inventory = new InventorySnapshot([
            new(7, new(InventoryListType.Main, 9), new ItemCore(401000039, ItemKind.Equipment, 999999998)
            {
                Durability = 45, ExpireTime = checked((int)now.AddHours(24).ToUnixTimeSeconds()), IsRental = true
            })
        ]) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.RentalPoint, 12) };
        byte[] state = RentalPackets.State(inventory, now);
        That(BinaryPrimitives.ReadUInt32LittleEndian(state) == 12 && BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(4)) == 1,
            "S2C988 只投影虚拟余额和有效租赁数量");
        That(BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8)) == 401000039 &&
            BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(12)) == now.AddHours(24).ToUnixTimeSeconds(),
            "S2C988 租赁期限使用Unix秒");
        byte[] delta = InventoryPackets.Delta(inventory, InventoryListType.Main, [new(InventoryListType.Main, 9)], now.ToUnixTimeSeconds());
        That(BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(3 + 0x38)) is >= 86399 and <= 86400,
            "租赁物品栏期限使用剩余秒而非Unix绝对时间");

        var valid = RentalRules.ValidateCharge(500, 10_000_000, 41, 410, 100);
        That(valid.Accepted && valid.PointsAfter == 141 && valid.GoldAfter == 9_959_000, "租赁点充值原子计算");
        That(!RentalRules.ValidateCharge(500, 1_000, 41, 410, 100).Accepted, "金币不足拒绝充值");
        var quest = QuestEligibilityTests.Quest(QuestRentalProgress.QuestId) with
        {
            Grade = "[system]", Condition = "[custom quest]", SubType = 0, IntData = [1, 0, 1],
            Npc = 2, CompleteNpc = -1, MinimumLevel = 10, MaximumLevel = 99,
            Definition = new([]) { Job = "[all]", AccountQuest = true }
        };
        That(QuestRentalProgress.Supports(quest), "租赁任务只开放已绑定事件17的完整Q8581合同");
        That(!QuestRentalProgress.Supports(quest with { Id = 8582 }) &&
            !QuestRentalProgress.Supports(quest with { IntData = [1, 0, 2] }) &&
            !QuestRentalProgress.Supports(quest with { Definition = quest.Definition! with { AccountQuest = false } }),
            "相邻任务号、未知事件值和非账号合同不借租赁事件获得支持");
        Console.WriteLine("PASS rental core assertions");
    }

    public static void RunResources(Character character, ItemResources resources)
    {
        var service = new RentalService(resources.Rental, resources.Items);
        var before = character with
        {
            Level = 1,
            Inventory = character.Inventory with { AccountWallet = character.Inventory.AccountWallet.Set(CurrencyKind.RentalPoint, 50) }
        };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        foreach (var (level, itemId, tier, cost) in new (byte, int, ushort, int)[]
        {
            (1, 408030023, 0, 3), (9, 408030023, 0, 3), (10, 408030023, 0, 3), (1, 408030030, 7, 40)
        })
        {
            var result = service.Rent(before with { Level = level }, itemId, 14, tier, now);
            That(result.Accepted && result.Plan!.After.AccountWallet.RentalPoints == 50 - cost,
                "租赁按请求商品和档位扣费，不另按角色等级拒绝: " + level + "/" + tier);
            var rented = result.Plan!.After.Find(new(InventoryListType.Main, 9))!;
            That(rented.Uid == 0 && rented.Core.ItemId == itemId && rented.Core.IsRental &&
                rented.ExpireTime == now.AddHours(24).ToUnixTimeSeconds() &&
                rented.Core.Durability == resources.Items.Get(itemId).Durability,
                "低等级领取保留模板耐久及24小时租期");
            if (level == 1)
                That(!InventoryMoveService.Allows(before, new(InventoryListType.Equipment, 12), rented.Core, resources.Items),
                    "租赁领取放开不改变普通穿戴等级条件");
        }
        That(!service.Rent(before, 408030023, 0, 0, now).Accepted, "租赁职业回显不匹配仍拒绝");
        That(!service.Rent(before, 408030023, 14, ushort.MaxValue, now).Accepted, "PVF未配置档位仍拒绝");
        That(!service.Rent(before, 408030030, 14, 0, now).Accepted, "物品不在请求PVF档位仍拒绝");
        var poor = before with { Inventory = before.Inventory with { AccountWallet = before.Inventory.AccountWallet.Set(CurrencyKind.RentalPoint, 2) } };
        That(!service.Rent(poor, 408030023, 14, 0, now).Accepted, "低等级放开后余额不足仍拒绝");
        var core = before.Inventory.Items.First().Core;
        var full = before with { Inventory = before.Inventory with
        {
            Items = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot), core)).ToImmutableArray()
        } };
        That(service.Rent(full, 408030023, 14, 0, now) is { Accepted: false, Error: 17 }, "租赁满包仍返回原错误");
        That(before.Inventory.Items.SequenceEqual(character.Inventory.Items) && before.Inventory.AccountWallet.RentalPoints == 50,
            "租赁规划与拒绝不修改原快照");
        Console.WriteLine("PASS rental resource assertions");
    }

    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("FAIL: " + message);
    }
}
