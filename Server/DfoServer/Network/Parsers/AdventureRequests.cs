using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal static class AdventureRequests
{
    public static ushort Target(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2) throw new InvalidDataException("冒险团查询必须为角色u16");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }
}
