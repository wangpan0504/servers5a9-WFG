using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal static class ClassChangeRequests
{
    public static ClassChangeRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (3 or 4) || body.Length == 4 && body[3] > 1)
            throw new InvalidDataException("职业互转正文或确认标记无效");
        return new(BinaryPrimitives.ReadInt16LittleEndian(body), body[2], body.Length == 4 && body[3] != 0, body.Length == 4);
    }
}
