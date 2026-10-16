using System.Buffers.Binary;

namespace DfoServer.Network.Builders;

internal static class ClassChangePackets
{
    public static byte[] Result(ushort error)
    {
        if (error == 0) return [1];
        byte[] body = [0, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), error);
        return body;
    }
}
