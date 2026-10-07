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
        int checks = 0;
        void Check(bool result, string reason)
        {
            if (!result) throw new Exception(reason);
            checks++;
        }
        void Reject(Action action)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid frame rejected");
        }

        Check(GameFrame.Crc([]) == 0, "empty CRC retains initialization and final inversion");
        for (int value = 0; value < 256; value++)
        {
            byte[] bytes = [(byte)value];
            Check(GameFrame.Crc(bytes) == Reference(bytes), "all single-byte CRC transitions match");
        }
        var random = new Random(20261007);
        foreach (int length in new[] { 0, 2, 3, 4, 7, 8, 15, 16, 17, 255, 256, 257, 4096, 65520, 65536 })
        {
            byte[] bytes = new byte[length + 17];
            random.NextBytes(bytes);
            for (int offset = 0; offset <= 17; offset++)
                Check(GameFrame.Crc(bytes.AsSpan(offset, length)) == Reference(bytes.AsSpan(offset, length)), "unaligned slices and boundary sizes match bitwise CRC");
        }
        foreach (int length in new[] { 0, 1, 31, 256, 65520 })
        {
            byte[] body = new byte[length];
            random.NextBytes(body);
            var frame = GameFrame.Build(1, 0xfffe, 0xffff, body);
            Check(frame.Length == 16 + length && BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(3)) == frame.Length &&
                frame[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 0xfffe &&
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)) == 0xffff && frame.AsSpan(13, 3).SequenceEqual(new byte[3]) &&
                frame.AsSpan(16).SequenceEqual(body), "S2C header, sequence and complete body unchanged");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(7)) == Reference(frame.AsSpan(11)), "S2C CRC covers sequence through body");
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
            Check(packet.Kind == 1 && packet.Opcode == 0x1234 && packet.Body.AsSpan().SequenceEqual(frame.AsSpan(13)), "C2S accepts independently generated CRC");
            Reject(() => GameFrame.ParseClient(frame.AsSpan(0, frame.Length - 1)));
            frame[11] ^= 1;
            Reject(() => GameFrame.ParseClient(frame));
        }
        Reject(() => GameFrame.Build(2, 0, 0, []));
        Reject(() => GameFrame.Build(1, 0, 0, new byte[65521]));
        Reject(() => GameFrame.ParseClient(new byte[12]));
        Reject(() => GameFrame.ParseClient(new byte[65537]));
        Console.WriteLine($"PASS {checks} 逐位 CRC 对照、完整帧和畸形输入断言");
    }
}
