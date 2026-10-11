using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class ConsumableUseRequests
{
    public static ConsumableUseRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 15) throw new InvalidDataException("普通消耗品使用正文宽度");
        return new(new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body)),
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]), BinaryPrimitives.ReadInt32LittleEndian(body[7..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[11..]));
    }
}
