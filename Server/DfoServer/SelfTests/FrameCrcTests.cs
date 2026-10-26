using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using DfoServer.Network;

namespace DfoServer;

internal static class FrameCrcTests
{
    // 独立保留原逐位算法，避免发送和接收同时改错仍然相互通过。
    static uint Reference(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0x4DB89129u);
        }
        return ~value;
    }

    public static void Run()
    {
        int checks = Count;
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "invalid frame rejected");
        }

        That(GameFrame.Crc([]) == 0, "empty CRC retains initialization and final inversion");
        for (int value = 0; value < 256; value++)
        {
            byte[] bytes = [(byte)value];
            That(GameFrame.Crc(bytes) == Reference(bytes), "all single-byte CRC transitions match");
        }
        var random = new Random(20261007);
        foreach (int length in new[] { 0, 2, 3, 4, 7, 8, 15, 16, 17, 255, 256, 257, 4096, 65520, 65536 })
        {
            byte[] bytes = new byte[length + 17];
            random.NextBytes(bytes);
            for (int offset = 0; offset <= 17; offset++)
                That(GameFrame.Crc(bytes.AsSpan(offset, length)) == Reference(bytes.AsSpan(offset, length)), "unaligned slices and boundary sizes match bitwise CRC");
        }
        foreach (int length in new[] { 0, 1, 31, 256, 65520 })
        {
            byte[] body = new byte[length];
            random.NextBytes(body);
            var frame = GameFrame.Build(1, 0xfffe, 0xffff, body);
            That(frame.Length == 16 + length && BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(3)) == frame.Length &&
                frame[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 0xfffe &&
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) == 0xffff && frame.AsSpan(13, 3).SequenceEqual(new byte[3]) &&
                frame.AsSpan(16).SequenceEqual(body), "S2C header, sequence and complete body unchanged");
            That(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(7)) == Reference(frame.AsSpan(11)), "S2C CRC covers sequence through body");
        }
        foreach (int length in new[] { 0, 1, 7, 257, 65523 })
        {
            byte[] frame = new byte[length + 13];
            random.NextBytes(frame);
            frame[0] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), 0x1234);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3), frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Reference(frame.AsSpan(11)));
            var packet = GameFrame.ParseClient(frame);
            That(packet.Kind == 1 && packet.Opcode == 0x1234 && packet.Body.AsSpan().SequenceEqual(frame.AsSpan(13)), "C2S accepts independently generated CRC");
            Reject(() => GameFrame.ParseClient(frame.AsSpan(0, frame.Length - 1)));
            frame[11] ^= 1;
            Reject(() => GameFrame.ParseClient(frame));
        }
        // 同一连接的首包与紧随帧可在头/正文任意位置短读；不能把一次Read当作一整包。
        foreach (bool channel in new[] { false, true })
        foreach (int fragment in new[] { 1, 3, 11 })
        {
            byte[] first = Request(15, new byte[4]), second = Request(37, new byte[8]);
            using var stream = new FragmentedStream([.. first, .. second], fragment);
            Bytes(GameFrame.ReadAsync(stream, channel, CancellationToken.None).GetAwaiter().GetResult(), first,
                "分段首包完整累计协议头与正文");
            Bytes(GameFrame.ReadAsync(stream, channel, CancellationToken.None).GetAwaiter().GetResult(), second,
                "粘连的后续包保留完整边界，不消耗下一帧字节");
            That(stream.Position == stream.Length, "两帧恰好消费，游戏与频道分别保留原13/11字节头");

            byte[] Request(ushort opcode, byte[] body)
            {
                int header = channel ? 11 : 13;
                byte[] frame = new byte[header + body.Length];
                frame[0] = 1;
                frame[1] = (byte)opcode;
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(channel ? 2 : 3), frame.Length);
                body.CopyTo(frame, header);
                if (!channel) BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Reference(frame.AsSpan(11)));
                return frame;
            }
        }
        Reject(() => GameFrame.Build(2, 0, 0, []));
        Reject(() => GameFrame.Build(1, 0, 0, new byte[65521]));
        Reject(() => GameFrame.ParseClient(new byte[12]));
        Reject(() => GameFrame.ParseClient(new byte[65537]));
        Console.WriteLine($"PASS {Count - checks} 逐位 CRC 对照、完整帧和畸形输入断言");
    }

    sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(fragment, buffer.Length)], cancellationToken);
    }
}
