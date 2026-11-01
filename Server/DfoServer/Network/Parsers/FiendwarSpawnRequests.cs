using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Parsers;

internal static class FiendwarSpawnRequests
{
    internal const ushort Opcode = 0x7FF2;
    internal const uint Magic = 0x31535746; // 仅解析旧夹具；正式租约实例拒绝无lease报告。
    internal const uint MagicV2 = 0x32535746;
    internal static FiendwarSpawnReport Parse(ReadOnlySpan<byte> body)
    {
        if (!(body.Length == 40 && BinaryPrimitives.ReadUInt32LittleEndian(body) == Magic ||
            body.Length == 48 && BinaryPrimitives.ReadUInt32LittleEndian(body) == 0x32535746))
            throw new InvalidDataException("漩涡原生召唤报告必须为完整FWS1");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(body[4..]), BinaryPrimitives.ReadUInt32LittleEndian(body[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[12..]), BinaryPrimitives.ReadUInt32LittleEndian(body[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[20..]), BinaryPrimitives.ReadUInt32LittleEndian(body[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[28..]), BinaryPrimitives.ReadUInt32LittleEndian(body[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[36..]), body.Length == 48 ? BinaryPrimitives.ReadUInt64LittleEndian(body[40..]) : 0);
    }
}
