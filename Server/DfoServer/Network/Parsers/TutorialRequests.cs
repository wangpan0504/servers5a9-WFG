using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct TutorialFlagRequest(byte Prefix, uint Progress, byte Commit);
internal static class TutorialRequests
{
    public static TutorialFlagRequest Flag(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6) throw new InvalidDataException("教程检查点正文必须为6B");
        return new(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body[5]);
    }
}
