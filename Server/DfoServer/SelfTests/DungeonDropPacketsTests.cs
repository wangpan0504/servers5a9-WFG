using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer;

internal static class DungeonDropPacketsTests
{
    public static void Run()
    {
        int checks = 0;
        foreach (ushort monster in new ushort[] { 0, 256, ushort.MaxValue })
            Check(DungeonPackets.MonsterDeath(monster, []).SequenceEqual(DungeonEntryProbe.BuildDeath(monster)),
                "零掉落保持已经实收的完整9B死亡合同");
        var first = new DungeonDropPacketItem(0xFEDCBA98, 17, new(1000, ItemKind.Consumable, 3), 531, 165);
        var second = new DungeonDropPacketItem(0xE1234567, 29, new(101010438, ItemKind.Equipment, 999999998)
        {
            Attr = 3, Durability = 41, Extension97 = 0xF1234567, GuardianGemKeys = 0xFEDCBA9876543210,
            Extension114 = new(0xFE, 0xEDCBA987), Extension121 = 0xFEDC
        }, 65000, 63000);
        var one = DungeonPackets.MonsterDeath(257, [first]);
        var two = DungeonPackets.MonsterDeath(258, [first, second]);
        Check(one.Length == 140 && two.Length == 271 && BinaryPrimitives.ReadUInt16LittleEndian(two) == 258
            && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(2)) == 2, "非零掉落每条131B且u16数量在头部");
        int cursor = 4;
        foreach (var drop in new[] { first, second })
        {
            Check(BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(cursor)) == drop.ObjectKey, "地面对象key完整保留u32高位");
            int core = cursor + 4;
            Check(BinaryPrimitives.ReadInt16LittleEndian(two.AsSpan(core)) == drop.RecordSlot
                && BinaryPrimitives.ReadInt32LittleEndian(two.AsSpan(core + 2)) == drop.Core.ItemId
                && BinaryPrimitives.ReadInt32LittleEndian(two.AsSpan(core + 6)) == drop.Core.Value,
                "123B记录与普通背包相同，堆叠数量或装备品级不混入对象key");
            Check(two[core + 10] == drop.Core.Attr && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 11)) == drop.Core.Durability
                && BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(core + 97)) == drop.Core.Extension97
                && BinaryPrimitives.ReadUInt64LittleEndian(two.AsSpan(core + 101)) == drop.Core.GuardianGemKeys
                && BinaryPrimitives.ReadUInt32LittleEndian(two.AsSpan(core + 114)) == drop.Core.Extension114.Value
                && two[core + 118] == drop.Core.Extension114.Flag
                && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 121)) == drop.Core.Extension121,
                "完整普通资产属性和扩展尾部不丢失或另造简化格式");
            Check(BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 123)) == drop.X
                && BinaryPrimitives.ReadUInt16LittleEndian(two.AsSpan(core + 125)) == drop.Y,
                "位置紧跟完整123B记录，第二条仍保持正确游标");
            cursor += 131;
        }
        Check(two.AsSpan(cursor).SequenceEqual(new byte[] { 0, 0, 255, 0, 0 }), "所有物品后再读取死亡可选标志、mode、grade及两尾标志");
        byte[] frame = GameFrame.Build(0, 38, 0x8123, two);
        Check(frame.Length == 16 + two.Length && frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 38
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) == 0x8123
            && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(7)) == GameFrame.Crc(frame.AsSpan(11))
            && frame.AsSpan(16).SequenceEqual(two), "非零掉落使用完整NOTI38帧、序号和CRC合同");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [first, first with { X = 1 }]), "重复地面key拒绝而不重复宣布");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = first.Core with { Value = 0 } }]), "零数量资产不产生成功包");
        Reject<NotSupportedException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = new(1, ItemKind.Avatar, 1) }]), "缺失时装RAW明确拒绝");
        Reject<NotSupportedException>(() => DungeonPackets.MonsterDeath(1, [first with { Core = new(1, ItemKind.Creature, 1) }]), "缺失宠物detail明确拒绝");
        var bound = Enumerable.Range(0, 500).Select(index => first with { ObjectKey = checked((uint)index) }).ToArray();
        Check(DungeonPackets.MonsterDeath(1, bound).Length == 65509, "帧上限内保留全部500条而不截断");
        Reject<InvalidDataException>(() => DungeonPackets.MonsterDeath(1, bound.Append(first with { ObjectKey = 501 }).ToArray()),
            "超过帧容量拒绝整个候选，不截断奖励");
        Console.WriteLine($"PASS {checks} 非零死亡掉落通知断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 死亡掉落通知: " + name);
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Check(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
        }
    }
}
