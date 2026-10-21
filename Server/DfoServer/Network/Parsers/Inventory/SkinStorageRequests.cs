using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class SkinStorageRequests
{
    public static MakeWeaponSkinRequest Make(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8) throw new InvalidDataException("武器幻化制作请求应为8字节");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body[6..]);
        int recipe = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (slot < 0 || recipe <= 0) throw new InvalidDataException("武器幻化槽位或配方编号无效");
        return new(body[0], recipe, new((InventoryListType)body[5], slot));
    }

    public static SelectSkinRequest Select(ReadOnlySpan<byte> body)
    {
        if (body.Length != 44) throw new InvalidDataException("皮肤选择请求应为44字节");
        int type = BinaryPrimitives.ReadInt32LittleEndian(body);
        int first = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (first < 0 || type != 1 && body[8..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("武器和伤害字体选择只允许一个非负编号");
        var additional = ImmutableArray.CreateBuilder<int>();
        if (type == 1)
            for (int at = 8; at < body.Length; at += 4)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
                if (id < 0) throw new InvalidDataException("技能插画编号不能为负数");
                additional.Add(id);
            }
        return new(type, first) { AdditionalIds = additional.ToImmutable() };
    }
}
