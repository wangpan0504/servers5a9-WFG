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

    // NOTI EVENT_INFO(108)，ARAD 026C1580 → 026C0EC0 整表替换事件集合 [026C1B00()+0x2c]：
    //   u16 n；n × { u16 事件号, u32, u32 长度+串, u32 长度+串, u8 扩展标志(0 不读扩展) }；
    //   随后 026BE900 读 u8 m；m × { u16 事件号, 12 × u32 }（旧集合含事件 9 时先多读 u8+u16 表，因此不下发事件 9）。
    // 额外事件沿用 2037 的已验证记录形状（u32 1、两空串、标志 0）；需要活动状态的事件另附全零数值块。
    public static byte[] EventInfoCompressed(IReadOnlyList<ushort>? extraEvents = null,
        IReadOnlyList<ushort>? activeEvents = null)
    {
        extraEvents ??= [];
        activeEvents ??= [];
        if (extraEvents.Any(id => id is 0 or 9 or SeriaLuckEventId) || extraEvents.Distinct().Count() != extraEvents.Count)
            throw new InvalidDataException("EVENT_INFO 附加事件无效");
        if (activeEvents.Count >= byte.MaxValue || activeEvents.Any(id => !extraEvents.Contains(id)) ||
            activeEvents.Distinct().Count() != activeEvents.Count)
            throw new InvalidDataException("EVENT_INFO 活动状态必须唯一、存在于附加事件集合且不超过254项");
        using var plain = new MemoryStream();
        using (var writer = new BinaryWriter(plain, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(checked((ushort)(1 + extraEvents.Count)));
            writer.Write(SeriaLuckEventId);
            writer.Write(1u);
            writer.Write(0);
            writer.Write(0);
            writer.Write((byte)0);
            foreach (ushort id in extraEvents)
            {
                writer.Write(id);
                writer.Write(1u);
                writer.Write(0);
                writer.Write(0);
                writer.Write((byte)0);
            }
            writer.Write((byte)(1 + activeEvents.Count));
            writer.Write(SeriaLuckEventId);
            writer.Write((uint)SeriaLuckValueMax);
            for (int index = 1; index < 12; index++)
                writer.Write(0u);
            foreach (ushort id in activeEvents)
            {
                writer.Write(id);
                for (int index = 0; index < 12; index++)
                    writer.Write(0u);
            }
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
