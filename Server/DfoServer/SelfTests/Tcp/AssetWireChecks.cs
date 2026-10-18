using System.Buffers.Binary;

namespace DfoServer.Testing;

internal sealed record AssetWireEntry(byte Slot, int TemplateId, uint Data, ushort Durability);
internal sealed record AssetWireResult(int ConsumedBytes, AssetWireEntry[] Entries);

internal static class AssetWireChecks
{
    // Actor 摘要不携带 Data 和耐久；返回的两个零值不用于与持久化资产比较。
    public static AssetWireResult ActorEntries(byte[] body, int countOffset)
    {
        var wire = Start(body, countOffset);
        int count = wire.Count();
        var rows = new List<AssetWireEntry>();
        var slots = new HashSet<byte>();
        for (int index = 0; index < count; index++)
        {
            byte slot = wire.Slot(slots);
            int template = wire.Template();
            wire.Native(4);
            wire.Zero(3);
            wire.Require(wire.U32() == 0 && wire.U32() == 0, "Actor 附加外观和丢弃字段");
            wire.Zero(1);
            rows.Add(new(slot, template, 0, 0));
        }
        return new(wire.ConsumedBytes, rows.ToArray());
    }

    public static AssetWireResult PlayerEntries(byte[] body, int countOffset)
    {
        var wire = Start(body, countOffset);
        int count = wire.Count();
        var rows = new List<AssetWireEntry>();
        var slots = new HashSet<byte>();
        for (int index = 0; index < count; index++)
        {
            byte slot = wire.Slot(slots);
            int template = wire.Template();
            uint data = wire.U32();
            wire.Zero(1);
            ushort durability = wire.U16();
            wire.Zero(2);
            wire.Require(wire.U32() == 0 && wire.U32() == 0, "Player 外观及 enchantIndex 清空");
            wire.Zero(2);
            wire.Require(wire.U16() == 0, "amplify_value 清空");
            wire.Require(wire.U8() == 0, "第一附加列表为空");
            wire.Require(wire.U32() == 0, "普通初装无到期时间");
            wire.Require(wire.U8() == 0, "第二附加列表为空");
            wire.Require(wire.U16() == 0, "Rune:u16");
            wire.Zero(1); // 随机属性列表为空。
            wire.Zero(10); // 023C3F9C..023C404A。
            wire.Native(4);
            wire.Native(8);
            wire.Zero(1);
            wire.Native(5);
            wire.Native(1);
            wire.Native(4);
            rows.Add(new(slot, template, data, durability));
        }
        wire.Require(wire.U32() == 0 && wire.U8() == 0, "Player 公共 scalar 与额外列表");
        return new(wire.ConsumedBytes, rows.ToArray());
    }

    private static WireReader Start(byte[] body, int countOffset)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (countOffset < 0 || countOffset >= body.Length)
            throw new InvalidDataException("装备 count 偏移越界");
        return new WireReader(body.AsSpan(countOffset));
    }

    // 从完整正文独立按消费者读宽读取；页内或缓冲区尾部缺字节不补零。
    private ref struct WireReader
    {
        private readonly ReadOnlySpan<byte> source;
        private int offset;

        public WireReader(ReadOnlySpan<byte> source)
        {
            this.source = source;
            offset = 0;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public void Require(bool value, string field)
        {
            if (!value)
                throw new InvalidDataException(field);
        }

        public int Count()
        {
            byte count = U8();
            Require(count <= 5, "当前装备 profile 最多五槽");
            return count;
        }

        public byte Slot(HashSet<byte> slots)
        {
            byte slot = U8();
            Require((slot is 0x0C or 0x0E or 0x10 or 0x11 or 0x12) && slots.Add(slot), "未知或重复槽位");
            return slot;
        }

        public int Template()
        {
            uint template = U32();
            Require(template is > 0 and <= int.MaxValue, "模板ID范围");
            return (int)template;
        }

        public uint Native(uint expected)
        {
            uint width = U32();
            Require(width == expected, "native 对象长度与 profile 类型不符");
            Zero(checked((int)width));
            return width;
        }

        public void Zero(int width)
        {
            ReadOnlySpan<byte> bytes = Take(width);
            foreach (byte value in bytes)
                Require(value == 0, "canonical 空状态包含非零字段");
        }

        public int ConsumedBytes => offset;

        private ReadOnlySpan<byte> Take(int width)
        {
            if (width > source.Length - offset)
                throw new InvalidDataException("装备正文截短");
            ReadOnlySpan<byte> bytes = source.Slice(offset, width);
            offset += width;
            return bytes;
        }
    }
}
