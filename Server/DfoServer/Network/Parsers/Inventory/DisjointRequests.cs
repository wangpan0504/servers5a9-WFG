using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class DisjointRequests
{
    public static DisjointRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 9)
            throw new InvalidDataException("Client装备分解请求必须为9字节");
        return new(new((InventoryListType)body[2], BinaryPrimitives.ReadInt16LittleEndian(body)),
            BinaryPrimitives.ReadInt16LittleEndian(body[3..]), BinaryPrimitives.ReadInt32LittleEndian(body[5..]));
    }
}
