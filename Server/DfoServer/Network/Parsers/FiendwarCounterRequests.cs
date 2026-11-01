using System.Buffers.Binary;
using DfoServer.Game.Titles;

namespace DfoServer.Network.Parsers;

internal static class FiendwarCounterRequests
{
    internal const ushort Opcode = 0x7FF1;
    internal const uint Magic = 0x31435746; // FWC1，本服私有协议。
    internal static FiendwarCounterReport Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 40 || BinaryPrimitives.ReadUInt32LittleEndian(body) != Magic)
            throw new InvalidDataException("漩涡原生破招报告必须为完整FWC1");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), BinaryPrimitives.ReadUInt32LittleEndian(body[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[12..]), BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[20..]), BinaryPrimitives.ReadUInt32LittleEndian(body[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[28..]), BinaryPrimitives.ReadUInt32LittleEndian(body[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[36..]));
    }
}
