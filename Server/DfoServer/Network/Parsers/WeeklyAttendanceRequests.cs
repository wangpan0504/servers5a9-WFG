using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class WeeklyAttendanceRequests
{
    public static bool Sign(ReadOnlySpan<byte> body) => body.Length == 13;
    public static bool OwnsBonus(ReadOnlySpan<byte> body) => body.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(body) == 2208;
    public static bool Bonus(ReadOnlySpan<byte> body) => OwnsBonus(body) && body.Length == 16 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) == 0 &&
        BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(body[12..]) == 0;
}
