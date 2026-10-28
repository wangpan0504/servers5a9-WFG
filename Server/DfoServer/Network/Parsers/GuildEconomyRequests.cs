using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class GuildEconomyRequests
{
    public static bool TryPurchase(ReadOnlySpan<byte> body, out uint content)
    {
        content = 0;
        if (body.Length != 8 || BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) != 0) return false;
        content = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return content is > 0 and <= int.MaxValue;
    }
}
