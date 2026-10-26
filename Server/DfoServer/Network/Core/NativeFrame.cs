using System.Buffers.Binary;
using System.IO.Compression;

namespace DfoServer.Network;

// Native 独立连接的外层；两个关联字段保持原始数值，不解释成角色或物件编号。
internal sealed record NativePacket(uint Association, uint Route, byte[] Body, int WireLength)
{
    public ushort Kind { get; init; } = 1;
}

internal static class NativeFrame
{
    const int HeaderLength = 13;
    const int MaximumBodyLength = ushort.MaxValue;

    public static NativePacket Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderLength || frame.Length > ushort.MaxValue ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame) > 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]) != frame.Length || frame[12] > 1)
            throw new InvalidDataException("Invalid native frame bounds");
        byte[] body;
        if (frame[12] == 0)
            body = frame[HeaderLength..].ToArray();
        else
        {
            var compressed = frame[HeaderLength..];
            if (compressed.Length < 6 || (compressed[0] & 15) != 8 || compressed[0] >> 4 > 7 ||
                ((compressed[0] << 8) | compressed[1]) % 31 != 0 || (compressed[1] & 32) != 0)
                throw new InvalidDataException("Invalid native zlib header");
            using var input = new MemoryStream(frame[HeaderLength..].ToArray(), false);
            using var decompressor = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = decompressor.Read(buffer)) != 0)
            {
                if (output.Length + count > MaximumBodyLength)
                    throw new InvalidDataException("Native body exceeds bounds");
                output.Write(buffer, 0, count);
            }
            body = output.ToArray();
            uint a = 1, b = 0;
            foreach (byte value in body) { a = (a + value) % 65521; b = (b + a) % 65521; }
            if ((b << 16 | a) != BinaryPrimitives.ReadUInt32BigEndian(compressed[^4..]))
                throw new InvalidDataException("Invalid native zlib checksum");
        }
        return new(BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]), body, frame.Length) { Kind = BinaryPrimitives.ReadUInt16LittleEndian(frame) };
    }

    public static byte[] Build(uint source, uint destination, ReadOnlySpan<byte> body)
    {
        if (source == 0 || destination == 0 || body.Length > ushort.MaxValue - HeaderLength)
            throw new InvalidDataException("Native reply bounds");
        var frame = new byte[HeaderLength + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), checked((ushort)frame.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), source);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), destination);
        body.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static async Task<NativePacket> ReadAsync(Stream stream, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        byte[] head = new byte[HeaderLength];
        await stream.ReadExactlyAsync(head, timeout.Token);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        if (length < HeaderLength || BinaryPrimitives.ReadUInt16LittleEndian(head) > 1 || head[12] > 1)
            throw new InvalidDataException("Invalid native frame bounds");
        byte[] frame = new byte[length];
        head.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(HeaderLength), timeout.Token);
        return Parse(frame);
    }
}
