using System.Buffers.Binary;
using DfoServer.Game.Creatures;

namespace DfoServer.Network.Parsers;

internal static class CreatureTransportRequests
{
    public static bool TryRead(ReadOnlySpan<byte> body, out CreatureTransportRequest request)
    {
        request = new(0, 0, 0, 0);
        if (body.Length != 9) return false;
        // ARAD 024C6450：u16源Main槽、u16目标Pet槽、u8模式、u32原始名册行号。
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]),
            body[4], BinaryPrimitives.ReadUInt32LittleEndian(body[5..]));
        return true;
    }
}
