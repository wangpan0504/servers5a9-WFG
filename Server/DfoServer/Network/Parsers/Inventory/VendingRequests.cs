using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class VendingRequests
{
    public static VendingRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 10) throw new InvalidDataException("售货机请求长度无效");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[8..]));
    }
}
