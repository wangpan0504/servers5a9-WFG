using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class AvatarCompoundRequests
{
    public const int BodySize = 22;

    public static AvatarCompoundRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != BodySize)
            throw new InvalidDataException("装扮合成请求正文必须为22字节");
        return new(
            BinaryPrimitives.ReadInt16LittleEndian(body),
            BinaryPrimitives.ReadInt16LittleEndian(body[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[4..]),
            BinaryPrimitives.ReadInt16LittleEndian(body[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[10..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[18..]),
            body[20],
            body[21]);
    }
}
