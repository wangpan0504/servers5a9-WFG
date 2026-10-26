using System.Buffers.Binary;
using System.IO.Compression;
using DfoServer.Network;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeFrameTests
{
    public static async Task RunAsync()
    {
        int before = Count;
        byte[] plain = Convert.FromHexString("0100100078563412EFCDAB9000010203");
        byte[] compressed = Convert.FromHexString("0100180078563412EFCDAB9001789C6364620600000D0007");
        foreach (byte[] frame in new[] { plain, compressed })
        {
            var packet = NativeFrame.Parse(frame);
            That(packet.Association == 0x12345678 && packet.Route == 0x90abcdef && packet.WireLength == frame.Length,
                "Native 独立向量保留两个 DWORD 和 u16 总长，不套用普通帧 CRC");
            Bytes(packet.Body, [1, 2, 3], "原始与 zlib 向量恢复相同正文");
            using var stream = new FragmentedStream(frame);
            Bytes((await NativeFrame.ReadAsync(stream, CancellationToken.None)).Body, [1, 2, 3],
                "Native 短读累积完整13字节头与正文");
        }
        Reject<InvalidDataException>(() => NativeFrame.Parse(plain[..12]), "拒绝短头");
        Reject<InvalidDataException>(() => NativeFrame.Parse(plain[..^1]), "拒绝总长不符");
        foreach (int position in new[] { 0, 2, 12 })
        {
            byte[] invalid = (byte[])plain.Clone(); invalid[position] = 2;
            Reject<InvalidDataException>(() => NativeFrame.Parse(invalid), "拒绝种类、长度和未知压缩标志");
        }
        byte[] checksum = (byte[])compressed.Clone(); checksum[^1] ^= 1;
        Reject<InvalidDataException>(() => NativeFrame.Parse(checksum), "拒绝损坏的 zlib 校验");
        byte[] truncated = compressed[..^4];
        BinaryPrimitives.WriteUInt16LittleEndian(truncated.AsSpan(2), (ushort)truncated.Length);
        Reject<InvalidDataException>(() => NativeFrame.Parse(truncated), "拒绝缺失 zlib 尾部");
        byte[] maximum = new byte[ushort.MaxValue];
        Bytes(NativeFrame.Parse(Compress(maximum)).Body, maximum, "压缩正文上限可完整读取");
        Reject<InvalidDataException>(() => NativeFrame.Parse(Compress(new byte[ushort.MaxValue + 1])), "解压放大越界立即拒绝");
        using var shortStream = new MemoryStream(plain[..^1]);
        await RejectAsync<EndOfStreamException>(() => NativeFrame.ReadAsync(shortStream, CancellationToken.None), "流截断不绑定会话");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var cancelledStream = new MemoryStream(plain);
        await RejectAsync<OperationCanceledException>(() => NativeFrame.ReadAsync(cancelledStream, cancelled.Token), "取消停止收帧");
        await NativeGameplayRecordsTests.RunAsync();
        Console.WriteLine($"PASS {Count - before} native frame assertions");
    }

    static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { That(true, message); return; }
        throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + message);
    }

    static byte[] Compress(byte[] body)
    {
        using var bytes = new MemoryStream();
        bytes.Write(new byte[13]);
        using (var zlib = new ZLibStream(bytes, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(body);
        byte[] frame = bytes.ToArray();
        frame[0] = 1; frame[12] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), checked((ushort)frame.Length));
        return frame;
    }

    sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }
}
