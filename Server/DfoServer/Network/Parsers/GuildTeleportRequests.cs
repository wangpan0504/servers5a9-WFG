using System.Buffers.Binary;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Parsers;

internal static class GuildTeleportRequests
{
    public static bool TryBegin(ReadOnlySpan<byte> body, out GuildTeleportRequest? request)
    {
        request = null;
        if (body.Length != 8 || body[6] != 5 || body[7] != 0) return false;
        ushort x = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), y = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (x > short.MaxValue || y > short.MaxValue) return false;
        request = new(new(body[0], body[1], x, y, body[6]), body[7]); return true;
    }
}
