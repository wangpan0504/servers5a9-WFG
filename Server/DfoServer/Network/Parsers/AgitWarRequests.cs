using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class AgitWarRequests
{
    public static bool IsTownInformation(ReadOnlySpan<byte> body) => body.SequenceEqual<byte>([2, 0, 0, 0]);

    public static bool TryQueryMode(ReadOnlySpan<byte> body, out uint mode)
    {
        mode = 0;
        if (body.Length == 1) { mode = body[0]; return mode <= 2; }
        if (body.Length != 4) return false;
        mode = BinaryPrimitives.ReadUInt32LittleEndian(body);
        return mode <= 2;
    }
}
