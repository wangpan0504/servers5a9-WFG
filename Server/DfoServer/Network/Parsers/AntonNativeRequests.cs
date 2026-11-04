using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal sealed record AntonNativeObservation(uint Serial, uint Dungeon, uint Map, uint Seed,
    uint Position, uint Sequence, uint NativeOpcode, byte[] Plain);

// ANO1是本服只观测协议，不能作为原生发送、生成成功ACK或客户端自回送。
internal static class AntonNativeRequests
{
    internal const ushort Opcode = 0x7FF4;
    internal const uint Magic = 0x314F4E41;
    internal const int HeaderLength = 36;
    internal const int MaximumPlainLength = 65520 - HeaderLength;

    internal static AntonNativeObservation Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length <= HeaderLength || body.Length > HeaderLength + MaximumPlainLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != Magic ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[32..]) != body.Length - HeaderLength)
            throw new InvalidDataException("安徒恩原生观测必须为完整ANO1");
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        uint dungeon = BinaryPrimitives.ReadUInt32LittleEndian(body[8..]);
        uint map = BinaryPrimitives.ReadUInt32LittleEndian(body[12..]);
        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(body[16..]);
        uint position = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(body[24..]);
        uint opcode = BinaryPrimitives.ReadUInt32LittleEndian(body[28..]);
        if (serial == 0 || dungeon != 218 || map == 0 || sequence == 0 || position > ushort.MaxValue || opcode is not (21 or 23))
            throw new InvalidDataException("安徒恩原生观测场景或事件无效");
        return new(serial, dungeon, map, seed, position, sequence, opcode, body[HeaderLength..].ToArray());
    }
}
