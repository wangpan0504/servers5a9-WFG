using System.Buffers.Binary;
using DfoServer.Game.CollectBox;

namespace DfoServer.Network.Parsers;

internal static class CollectBoxRequests
{
    public static byte Select(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1 || body[0] == 0)
            throw new InvalidDataException("选择宝珠收集箱请求必须为非零u8");
        return body[0];
    }

    public static CollectBoxAddRequest Add(ReadOnlySpan<byte> body)
    {
        if (body.Length != 8 || body[0] == 0 || body[1] != 0)
            throw new InvalidDataException("放入宝珠收集箱请求必须为box、零保留位、来源u16和物品i32");
        int itemId = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        if (itemId <= 0)
            throw new InvalidDataException("放入宝珠收集箱物品必须为正数");
        return new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), itemId);
    }

    public static CollectBoxRemoveRequest Remove(ReadOnlySpan<byte> body)
    {
        if (body.Length != 5 || body[0] == 0)
            throw new InvalidDataException("取出宝珠收集箱请求必须为box和物品i32");
        int itemId = BinaryPrimitives.ReadInt32LittleEndian(body[1..]);
        if (itemId <= 0)
            throw new InvalidDataException("取出宝珠收集箱物品必须为正数");
        return new(body[0], itemId);
    }

    public static CollectBoxExtendRequest Extend(ReadOnlySpan<byte> body)
    {
        var add = Add(body);
        return new(add.Box, add.SourceSlot, add.ItemId);
    }
}
