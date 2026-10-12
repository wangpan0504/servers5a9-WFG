using System.IO.Compression;

namespace DfoServer.Network.Builders;

internal static class SeriaLuckPackets
{
    public const ushort SeriaLuckEventId = 2037;
    public const int SeriaLuckValueMax = 8;

    public static byte[] Gauge(int before, int after)
    {
        ValidateStage(before);
        ValidateStage(after);
        return [(byte)(before * 5), (byte)(after * 5)];
    }

    public static byte[] EventInfoCompressed()
    {
        using var plain = new MemoryStream();
        using (var writer = new BinaryWriter(plain, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)1);
            writer.Write(SeriaLuckEventId);
            writer.Write(1u);
            writer.Write(0);
            writer.Write(0);
            writer.Write((byte)0);
            writer.Write((byte)1);
            writer.Write(SeriaLuckEventId);
            writer.Write((uint)SeriaLuckValueMax);
            for (int index = 1; index < 12; index++)
                writer.Write(0u);
        }
        plain.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            plain.CopyTo(zlib);
        return compressed.ToArray();
    }

    static void ValidateStage(int stage)
    {
        if (stage is < 0 or > SeriaLuckValueMax)
            throw new InvalidDataException($"无效赛丽亚幸运阶段: {stage}");
    }
}
