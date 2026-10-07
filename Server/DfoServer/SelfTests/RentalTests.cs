using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using DfoServer.Game.Rental;
using DfoServer.Network.Builders.Inventory;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class RentalTests
{
    public static void Run()
    {
        byte[] charge = new byte[21];
        BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(charge.AsSpan(17), 41);
        var chargeRequest = RentalRequests.Charge(charge);
        Check(chargeRequest.Type == 1 && chargeRequest.Count == 41, "C2S1002 21字节请求解析偏移");
        Reject(() => RentalRequests.Charge(charge[..20]), "C2S1002 非法长度拒绝");

        byte[] rent = Convert.FromHexString("5CD9C67B80207A3A401833380067C6E61700000700");
        var rentRequest = RentalRequests.Equipment(rent);
        Check(rentRequest.ItemId == 401000039 && rentRequest.Job == 0 && rentRequest.Tier == 7, "C2S1001 itemId/job/tier解析");

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var inventory = new InventorySnapshot([
            new(7, new(InventoryListType.Main, 9), new ItemCore(401000039, ItemKind.Equipment, 999999998)
            {
                Durability = 45, ExpireTime = checked((int)now.AddHours(24).ToUnixTimeSeconds()), IsRental = true
            })
        ]) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.RentalPoint, 12) };
        byte[] state = RentalPackets.State(inventory, now);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(state) == 12 && BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(4)) == 1,
            "S2C988 只投影虚拟余额和有效租赁数量");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8)) == 401000039 &&
            BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(12)) == now.AddHours(24).ToUnixTimeSeconds(),
            "S2C988 租赁期限使用Unix秒");
        byte[] delta = InventoryPackets.Delta(inventory, InventoryListType.Main, [new(InventoryListType.Main, 9)], now.ToUnixTimeSeconds());
        Check(BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(3 + 0x38)) is >= 86399 and <= 86400,
            "租赁物品栏期限使用剩余秒而非Unix绝对时间");

        var valid = RentalRules.ValidateCharge(500, 10_000_000, 41, 410, 100);
        Check(valid.Accepted && valid.PointsAfter == 141 && valid.GoldAfter == 9_959_000, "租赁点充值原子计算");
        Check(!RentalRules.ValidateCharge(500, 1_000, 41, 410, 100).Accepted, "金币不足拒绝充值");
        Console.WriteLine("PASS rental core assertions");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
    static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("FAIL: " + message);
    }
}
