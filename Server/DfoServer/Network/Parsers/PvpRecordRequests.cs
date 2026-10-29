using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct PvpRecordRequest(ushort Target, byte Self, byte View, int TeamMember);

internal static class PvpRecordRequests
{
    internal static bool TryRead(ReadOnlySpan<byte> body, out PvpRecordRequest request)
    {
        request = default;
        if (body.Length != 8) return false;
        request = new(BinaryPrimitives.ReadUInt16LittleEndian(body), body[2], body[3],
            BinaryPrimitives.ReadInt32LittleEndian(body[4..]));
        return request.Target is > 0 and < ushort.MaxValue && request.Self <= 1 && request.View is >= 2 and <= 6 &&
            request.TeamMember is >= -1 and <= 2;
    }
}
