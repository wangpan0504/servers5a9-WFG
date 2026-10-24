using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal static class DungeonAltarRequests
{
    // V31 03B3CE30/03B3CF50只写u8 count与u16 key；这里没有CMD39的统计或校验尾。
    public static bool TryParseDeaths(ReadOnlySpan<byte> body, out ImmutableArray<ushort> keys)
    {
        keys = [];
        if (body.Length == 0 || body.Length != 1 + body[0] * 2) return false;
        var result = ImmutableArray.CreateBuilder<ushort>(body[0]);
        var unique = new HashSet<ushort>();
        for (int at = 1; at < body.Length; at += 2)
        {
            ushort key = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
            if (key == 0 || !unique.Add(key)) return false;
            result.Add(key);
        }
        keys = result.MoveToImmutable();
        return true;
    }
}
