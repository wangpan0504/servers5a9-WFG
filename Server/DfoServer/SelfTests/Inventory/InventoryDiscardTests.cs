using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class InventoryDiscardTests
{
    public static void RunCore()
    {
        int checks = Count;
        var captured = InventoryRequests.Discard(Convert.FromHexString("AD050A010013000100000000"));
        That(captured == new InventoryDiscardRequest(1453, 266, 0, 19, 1, 0), "实收12B CMD47保留坐标、容器、u16槽位、u32数量和末byte");
        for (int length = 0; length < 12; length++)
            Reject<InvalidDataException>(() => InventoryRequests.Discard(new byte[length]), "丢弃短包拒绝");
        Reject<InvalidDataException>(() => InventoryRequests.Discard(new byte[13]), "丢弃长包拒绝");
        var wide = InventoryRequests.Discard(Enumerable.Repeat((byte)255, 12).ToArray());
        That(wide.Slot == ushort.MaxValue && wide.Count == uint.MaxValue && wide.Tail == 255, "请求最大值不截断为有符号数量或业务模式");
        var template = new ItemTemplate(1000, ItemKind.Consumable, 0, 1, 0, 100, 1, 0, 1, "[free]", 0, 0, "fixture");
        var source = new InventoryItem(7, new(InventoryListType.Main, 61), new(1000, ItemKind.Consumable, 10)
        {
            Attr = 7, Marker16 = 23, SortLockFlag = 1, Random0 = new(1, 2, 3), Extension97 = 0x12345678,
            GuardianGemKeys = 0x123456789ABCDEF0, Extension114 = new(9, 17), Extension121 = 0x4321
        });
        var character = new Character(1, 0, "discard", 1, new Location(38, 2, 100, 249, 0), null)
        { Inventory = new(ImmutableArray.Create(source)), DungeonState = CharacterDungeonState.NewOrdinary };
        var request = new InventoryDiscardRequest(1453, 266, 0, 61, 4, 1);
        var half = InventoryDiscardPlan.Prepare(character, request, template, 100);
        That(half.Error == 0 && half.Plan!.GroundCore == (source.Core with { Value = 4 }) &&
            half.Plan.Inventory.After.Find(source.Position) == (source with { Core = source.Core with { Value = 6 } }),
            "部分丢弃足额减堆并保留所有属性、排序锁及剩余实例UID，不重新生成核心");
        That(InventoryDiscardPlan.Prepare(character, request with { Count = 10 }, template, 100).Plan!.Inventory.After.Find(source.Position) is null,
            "整堆丢弃清源槽，不制造零数量实例");
        foreach (uint count in new uint[] { 0, 11, uint.MaxValue })
            That(InventoryDiscardPlan.Prepare(character, request with { Count = count }, template, 100).Error == 17,
                "地面丢弃严格按数量拒绝，不能套CMD18整堆归一规则");
        That(InventoryDiscardPlan.Prepare(character, request, template with { AttachType = "[untradable]" }, 100).Error == 19,
            "不可交易物不能进入可拾取地面");
        That(InventoryDiscardPlan.Prepare(character with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }]) },
            request, template, 100).Error == 19, "安全锁保护源实例");
        That(InventoryDiscardPlan.Prepare(character with { Inventory = new([source with { Core = source.Core with { ExpireTime = 100 } }]) },
            request, template, 100).Error == 104, "已过期物品使用本版拒绝分支");
        var equipment = source with { Position = new(InventoryListType.Equipment, 12), Core = new(1001, ItemKind.Equipment, 0) { SealFlag = 1 } };
        var worn = character with { Inventory = new([equipment]) };
        var equippedRequest = request with { List = 3, Slot = 12, Count = 1 };
        That(InventoryDiscardPlan.Prepare(worn, equippedRequest, template with { Id = 1001, Kind = ItemKind.Equipment, AttachType = "[sealing]" }, 100).Plan!.GroundCore == equipment.Core,
            "穿戴装备仍可依法丢弃，零品级完整保留，不额外要求先卸装");
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 2);
        var map = new DungeonMap("map", new(0, 0), 1, "map/fixture.map");
        var ground = new DungeonGroundObject(Guid.NewGuid(), identity, new(1, 256, map, request.X, request.Y, 0), half.Plan!.GroundCore)
        { InventoryOrigin = new(source, 4, 0) };
        byte[] notice = InventoryDiscardPackets.Ground(1, ground);
        That(notice.Length == 136 && BinaryPrimitives.ReadUInt16LittleEndian(notice) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) == 1453 && BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(4)) == 266 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(6)) == 256 &&
            BinaryPrimitives.ReadInt32LittleEndian(notice.AsSpan(12)) == 1000 && BinaryPrimitives.ReadInt32LittleEndian(notice.AsSpan(16)) == 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(notice.AsSpan(107)) == source.Core.Extension97 &&
            BinaryPrimitives.ReadUInt64LittleEndian(notice.AsSpan(111)) == source.Core.GuardianGemKeys &&
            notice.AsSpan(133).SequenceEqual(new byte[] { 0, 1, 0 }), "NOTI40独立核对136B坐标、key、123B完整核心、mode与归属");
        Bytes(InventoryDiscardPackets.Accepted(request), [1, 0, 61, 0, 4, 0, 0, 0], "ACK47回实际扣除4件，不填剩余6件");
        Bytes(InventoryDiscardPackets.Rejected(3, 104), [0, 104, 0, 3], "失败仍有容器byte收尾，不能套3B通用ACK");
        Console.WriteLine($"PASS {Count - checks} CMD47库存规划、实收Parser与原生ACK/地面通知断言");
    }

}
