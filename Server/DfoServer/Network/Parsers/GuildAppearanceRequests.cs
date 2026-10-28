using System.Buffers.Binary;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Parsers;

internal static class GuildAppearanceRequests
{
    public static GuildAppearanceChange Mark(ReadOnlySpan<byte> body)
    {
        if (body.Length == 1) return new(false, body[0], LegacyMark: true);
        if (body.Length == 4)
        {
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if ((packed & 0xFF00FF00u) == 0) return new(false, packed);
        }
        throw new InvalidDataException("Invalid guild mark request");
    }
    public static GuildAppearanceChange Aura(ReadOnlySpan<byte> body) => body.Length == 8
        ? new(true, AuraMark: BinaryPrimitives.ReadUInt32LittleEndian(body),
            AuraFrame: BinaryPrimitives.ReadUInt32LittleEndian(body[4..]))
        : throw new InvalidDataException("Invalid guild aura request");
}
