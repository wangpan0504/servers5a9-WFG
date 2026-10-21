using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class InventoryDurabilityTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(InventoryRequests.DurabilityLoss([12]) == 12 && InventoryRequests.DurabilityLoss([255]) == 255,
            "实收48/0C只保留穿戴槽，不将字节当作损失数量或位图");
        foreach (int length in new[] { 0, 2, 5 })
            Reject<InvalidDataException>(() => InventoryRequests.DurabilityLoss(new byte[length]), "未知耐久报告长度拒绝");
        var template = new ItemTemplate(100, ItemKind.Equipment, 12, 1, 20, 1, 1, 0, 1, "[free]", ushort.MaxValue, 0, "durable");
        var source = new InventoryItem(7, new(InventoryListType.Equipment, 12), new(100, ItemKind.Equipment, 73)
        {
            Durability = 2, Attr = 7, EnchantCardId = 3175, SortLockFlag = 1, EquipmentLockId = 3,
            Random0 = new(1, 2, 3), Extension97 = 0x12345678, GuardianGemKeys = 0x123456789ABCDEF0
        });
        var spare = source with { Uid = 8, Position = new(InventoryListType.Main, 12) };
        var character = new Character(1, 0, "Wear", 9, new(38, 2, 100, 249, 0), null)
        { Inventory = new([source, spare]) { Gold = 123 }, Level = 1 };
        var first = InventoryDurabilityService.Plan(character, 12, template, DateTimeOffset.UnixEpoch);
        var worn = first.Plan!.After.Find(source.Position)!;
        That(first.Accepted && first.Plan.Changed && worn.Uid == source.Uid && worn.Position == source.Position &&
            worn.Core.Durability == 1 && (worn.Core with { Durability = 2 }) == source.Core &&
            first.Plan.After.Find(spare.Position) == spare && first.Plan.After.Gold == 123 && source.Core.Durability == 2,
            "一次报告仅扣当前穿戴一格；UID、全部非耐久字段、锁、备用物和金币不变，等级1不套旧版十级门槛");
        byte[] body = InventoryPackets.Delta(first.Plan.After, InventoryListType.Equipment, [source.Position]);
        That(body.Length == 126 && body[0] == 3 && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1)) == 1 &&
            BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(3)) == 12 && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(14)) == 1,
            "耐久投影仅一行完整123B装备核心，不使用全局179标量或Actor重建包");
        var second = InventoryDurabilityService.Plan(character with { Inventory = first.Plan.After }, 12, template, DateTimeOffset.UnixEpoch);
        var zero = InventoryDurabilityService.Plan(character with { Inventory = second.Plan!.After }, 12, template, DateTimeOffset.UnixEpoch);
        That(second.Plan.After.Find(source.Position)!.Core.Durability == 0 && zero.Accepted && !zero.Plan!.Changed,
            "合法重复报告再次扣减，零耐久钳制且不制造空写入；不按相同报文伪造永久幂等");
        That(!InventoryDurabilityService.Plan(character, 13, template, DateTimeOffset.UnixEpoch).Accepted &&
            !InventoryDurabilityService.Plan(character, 12, template with { Id = 101 }, DateTimeOffset.UnixEpoch).Accepted,
            "空穿戴槽及错模板不能借用Main中的同槽装备");
        That(!InventoryDurabilityService.Plan(character with { Inventory = new([source with { Core = source.Core with { ExpireTime = 1 } }]) },
            12, template, DateTimeOffset.FromUnixTimeSeconds(2)).Accepted &&
            !InventoryDurabilityService.Plan(character, 12, template with { Kind = ItemKind.CreatureEquipment }, DateTimeOffset.UnixEpoch).Accepted,
            "到期或非普通装备不沿此报告损耗");
        That(!InventoryDurabilityService.Plan(character, 12, template with { Durability = 0 }, DateTimeOffset.UnixEpoch).Plan!.Changed,
            "零最大耐久模板不凭客户端报告制造耐久变化");
        Console.WriteLine($"PASS {Count - checks} 耐久报告、完整核心、单行投影和零变化断言");
    }
}
