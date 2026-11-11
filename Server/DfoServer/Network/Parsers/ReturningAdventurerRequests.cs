using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct ReturningAdventurerRequest(uint Action, uint Argument);
internal static class ReturningAdventurerRequests
{
    public static ReturningAdventurerRequest? Read(ReadOnlySpan<byte> body) => body.Length == 8
        ? new(BinaryPrimitives.ReadUInt32LittleEndian(body), BinaryPrimitives.ReadUInt32LittleEndian(body[4..])) : null;
}
