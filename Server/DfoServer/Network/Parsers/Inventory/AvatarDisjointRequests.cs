using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarDisjointRequests
{
    public static AvatarDisjointRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6)
            throw new InvalidDataException("Client时装分解请求必须为6字节");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(body[2..]);
        if (slot < 0 || itemId is 0 or > int.MaxValue)
            throw new InvalidDataException("Client时装分解请求字段无效");
        return new(slot, (int)itemId);
    }
}
