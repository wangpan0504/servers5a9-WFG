using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class DungeonDropPacketsTests
{
    public static void Run()
    {
        int checks = Count;
        foreach (ushort monster in new ushort[] { 0, 256, ushort.MaxValue })
            That(DungeonPackets.MonsterDeath(monster, []).SequenceEqual(DungeonEntryProbe.BuildDeath(monster)),
                "零掉落保持已经实收的完整9B死亡合同");
        var first = new DungeonDropPacketItem(0xFEDCBA98, 17, new(1000, ItemKind.Consumable, 3), 531, 165);
        var second = new DungeonDropPacketItem(0xE1234567, 29, new(101010438, ItemKind.Equipment, 999999998)
        {
            Attr = 3, Durability = 41, Extension97 = 0xF1234567, GuardianGemKeys = 0xFEDCBA9876543210,
            Extension114 = new(0xFE, 0xEDCBA987), Extension121 = 0xFEDC
        }, 65000, 63000);
        var one = DungeonPackets.MonsterDeath(257, [first]);
        var two = DungeonPackets.MonsterDeath(258, [first, second]);
        Bytes(DungeonPackets.MonsterDeath(256,[],new(2,3)),[0,1,0,0,0,0,255,1,2,3,0],
            "dummy死亡在本版38尾部插入更新标志及两个坐标，死亡标志仍在最后");
        var movedBoss = ExclusiveDropPackets.MonsterDeath(258,[first,second],51,new(2,3));
        That(movedBoss.AsSpan(0,two.Length-5).SequenceEqual(two.AsSpan(0,two.Length-5)) &&
            movedBoss.AsSpan(two.Length-5).SequenceEqual(new byte[] { 0,0,255,1,2,3,0 }),
            "有掉落的dummy死亡保留完整物品列表和游标，独占表包装不吞掉目标坐标");
        foreach (var invalid in new[] { new DungeonPoint(-1,0),new(0,-1),new(256,0),new(0,256) })
            Reject<InvalidDataException>(()=>DungeonPackets.MonsterDeath(256,[],invalid),"目标坐标不截断到错误格子");
        var gold = DungeonPackets.MonsterDeath(259, [new(1234, 0, null, 0, 0) { GoldAmount = 321 }]);
        That(gold.Length == 140 && BinaryPrimitives.ReadUInt32LittleEndian(gold.AsSpan(4)) == 1234 &&
            BinaryPrimitives.ReadInt32LittleEndian(gold.AsSpan(10)) == 0 && BinaryPrimitives.ReadInt32LittleEndian(gold.AsSpan(14)) == 321 &&
            gold.AsSpan(18, 117).SequenceEqual(new byte[117]) && gold.AsSpan(135).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }),
            "金币使用本版完整地面key与123B记录，模板0/金额和零创建上下文不伪造实体Core");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [new(1234, 0, null, 0, 0)]), "空Core与零金币不能形成合法地面记录");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [first with { GoldAmount = 1 }]), "一条地面记录不能同时包含实体和金币");
        That(one.Length == 140 && two.Length == 271 && BinaryPrimitives.ReadUInt16LittleEndian(two) == 258
            && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(2)) == 2, "非零掉落每条131B且u16数量在头部");
        int cursor = 4;
        foreach (var drop in new[] { first, second })
        {
            var expectedCore = drop.Core ?? throw new InvalidOperationException("固定物品fixture缺少Core");
            That(BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(cursor)) == drop.ObjectKey, "地面对象key完整保留u32高位");
            int core = cursor + 4;
            That(BinaryPrimitives.ReadInt16LittleEndian(two.AsSpan(core)) == drop.RecordSlot
                && BinaryPrimitives.ReadInt32LittleEndian(two.AsSpan(core + 2)) == expectedCore.ItemId
                && BinaryPrimitives.ReadInt32LittleEndian(two.AsSpan(core + 6)) == expectedCore.Value,
                "123B记录与普通背包相同，堆叠数量或装备品级不混入对象key");
            That(two[core + 10] == expectedCore.Attr && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 11)) == expectedCore.Durability
                && BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(core + 97)) == expectedCore.Extension97
                && BinaryPrimitives.ReadUInt64LittleEndian(two.AsSpan(core + 101)) == expectedCore.GuardianGemKeys
                && BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(core + 114)) == expectedCore.Extension114.Value
                && two[core + 118] == expectedCore.Extension114.Flag
                && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 121)) == expectedCore.Extension121,
                "完整普通资产属性和扩展尾部不丢失或另造简化格式");
            That(BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 123)) == drop.ContextValue
                && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 125)) == drop.VisibilityFilter,
                "两个创建上下文紧跟完整123B记录，第二条仍保持正确游标；非零不证明普通可见");
            cursor += 131;
        }
        That(two.AsSpan(cursor).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }), "所有物品后再读取死亡可选标志、mode、grade及两尾标志");
        byte[] frame = GameFrame.Build(0, 38, 0x8123, two);
        That(frame.Length == 16 + two.Length && frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 38
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) == 0x8123
            && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(7)) == GameFrame.Crc(frame.AsSpan(11))
            && frame.AsSpan(16).SequenceEqual(two), "非零掉落使用完整NOTI38帧、序号和CRC合同");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [first, first with { ContextValue = 1 }]), "重复地面key拒绝而不重复宣布");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = first.Core! with { Value = 0 } }]), "零数量资产不产生成功包");
        Reject<NotSupportedException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = new(1, ItemKind.Avatar, 1) }]), "缺失时装RAW明确拒绝");
        Reject<NotSupportedException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = new(1, ItemKind.Creature, 1) }]), "缺失宠物detail明确拒绝");
        var bound = Enumerable.Range(0, 500).Select(index => first with { ObjectKey = checked((uint)index) }).ToArray();
        That(DungeonPackets.MonsterDeath(1, bound).Length == 65509, "帧上限内保留全部500条而不截断");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, bound.Append(first with { ObjectKey = 501 }).ToArray()),
            "超过帧容量拒绝整个候选，不截断奖励");
        Console.WriteLine($"PASS {Count - checks} 非零死亡掉落通知断言");

    }
}
