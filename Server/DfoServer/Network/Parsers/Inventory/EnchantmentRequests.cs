using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class EnchantmentRequests
{
    public static EnchantmentRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 10)
            throw new InvalidDataException("宝珠附魔请求正文宽度无效");
        ushort source = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        ushort target = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (source > short.MaxValue || target > short.MaxValue)
            throw new InvalidDataException("宝珠附魔请求槽位超出范围");
        return new(new((InventoryListType)body[0], (short)source), new((InventoryListType)body[3], (short)target),
            BinaryPrimitives.ReadUInt32LittleEndian(body[6..]));
    }
}
