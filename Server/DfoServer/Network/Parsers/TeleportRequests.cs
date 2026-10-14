using System.Buffers.Binary;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Network.Parsers;

internal static class TeleportRequests
{
    public static PotionTeleportRequest Potion(ReadOnlySpan<byte> body)
    {
        if (body.Length != 14) throw new InvalidDataException("Teleport potion body width");
        return new(BinaryPrimitives.ReadInt16LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[2..]), body[6], Position(body[7..]));
    }

    public static SoloTeleportRequest Solo(ReadOnlySpan<byte> body)
    {
        if (body.Length != 15) throw new InvalidDataException("Solo teleport body width");
        return new(BinaryPrimitives.ReadInt32LittleEndian(body), BinaryPrimitives.ReadInt32LittleEndian(body[4..]), Position(body[8..]));
    }

    static Location Position(ReadOnlySpan<byte> body) => new(body[0], body[1],
        BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), body[6]);
}
