using System.Buffers.Binary;

namespace DfoServer.Network;

internal sealed record Packet(byte Kind, ushort Opcode, byte[] Body);
internal static class GameFrame
{
    static readonly uint[] CrcTable = CreateCrcTable();

    static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint x = value;
            for (int bit = 0; bit < 8; bit++)
                x = (x >> 1) ^ ((x & 1) != 0 ? 0x4DB89129u : 0);
            table[value] = x;
        }
        return table;
    }

    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint x = uint.MaxValue;
        foreach (byte b in bytes)
            x = (x >> 8) ^ CrcTable[(x ^ b) & 255];

        return ~x;
    }

    public static byte[] Build(byte kind, ushort opcode, ushort sequence, ReadOnlySpan<byte> body)
    {
        if (kind > 1 || body.Length > 65520)
            throw new InvalidDataException("Frame bounds");
        byte[] result = new byte[16 + body.Length];
        result[0] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(3), result.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(11), sequence);
        body.CopyTo(result.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(7), Crc(result.AsSpan(11)));
        return result;
    }

    public static Packet ParseClient(ReadOnlySpan<byte> frame)
    {
        if (frame.Length is < 13 or > 65536 || frame[0] != 1 || BinaryPrimitives.ReadInt32LittleEndian(frame[3..]) != frame.Length || Crc(frame[11..]) != BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]))
            throw new InvalidDataException("Invalid client frame/CRC");
        return new(frame[0], BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]), frame[13..].ToArray());
    }

    public static async Task<byte[]> ReadAsync(Stream stream, bool channel, CancellationToken stop)
    {
        int header = channel ? 11 : 13, offset = channel ? 2 : 3;
        byte[] head = new byte[header];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await stream.ReadExactlyAsync(head, timeout.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(offset));
        if (length < header || length > 65536)
            throw new InvalidDataException("Invalid packet length");
        byte[] bytes = new byte[length];
        head.CopyTo(bytes, 0);
        await stream.ReadExactlyAsync(bytes.AsMemory(header), timeout.Token);
        return bytes;
    }
}
