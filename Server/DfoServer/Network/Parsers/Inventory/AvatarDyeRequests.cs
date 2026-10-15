using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarDyeRequests
{
    public static AvatarDyeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("漂染请求必须为两个i16槽位");
        return new(BinaryPrimitives.ReadInt16LittleEndian(body), BinaryPrimitives.ReadInt16LittleEndian(body[2..]));
    }
}
