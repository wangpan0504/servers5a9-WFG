using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class TalismanRequests
{
    public static RuneEquipRequest Equip(ReadOnlySpan<byte> body)
    {
        // 006999D0：目标u16、源引用u16、模板u32；不带13B表单前缀。
        RequireLength(body, 8);
        int itemId = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (itemId <= 0) throw new InvalidDataException("符文装载模板编号无效");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body),
            BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), itemId);
    }

    public static ushort Remove(ReadOnlySpan<byte> body)
    {
        RequireLength(body, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    public static TalismanRefineRequest Refine(ReadOnlySpan<byte> body)
    {
        // 00691F30把通用表单转换成21B：13B不透明前缀、Main材料格i32、护石源引用i32。
        RequireLength(body, 21);
        return new(Slot(BinaryPrimitives.ReadInt32LittleEndian(body[13..])),
            Slot(BinaryPrimitives.ReadInt32LittleEndian(body[17..])));
    }

    public static TalismanCompoundRequest Compound(ReadOnlySpan<byte> body)
    {
        // 015532A0/00691C10：13B前缀、两个u8、u32件数、u32输入品级、五个i16源引用。
        RequireLength(body, 33);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body[15..]);
        int rule = BinaryPrimitives.ReadInt32LittleEndian(body[19..]);
        if (count is < 1 or > 5 || rule is < 0 or > 6)
            throw new InvalidDataException("护石合成件数或规则编号无效");
        var slots = ImmutableArray.CreateBuilder<short>((int)count);
        for (int i = 0; i < 5; i++)
        {
            short slot = BinaryPrimitives.ReadInt16LittleEndian(body[(23 + i * 2)..]);
            if (i < count)
            {
                // 008AC2F0按堆叠数量多次追加同格；业务层按出现次数复验和扣减。
                if (slot < 0)
                    throw new InvalidDataException("护石合成源引用无效");
                slots.Add(slot);
            }
            else if (slot != -1)
                throw new InvalidDataException("护石合成空材料位必须为0xFFFF");
        }
        return new(body[13], body[14], rule, slots.MoveToImmutable());
    }

    static short Slot(int value) => value is >= 0 and <= short.MaxValue ? (short)value :
        throw new InvalidDataException("护石操作物品引用越界");

    static void RequireLength(ReadOnlySpan<byte> body, int expected)
    {
        if (body.Length != expected) throw new InvalidDataException("护石操作请求正文长度无效");
    }
}
