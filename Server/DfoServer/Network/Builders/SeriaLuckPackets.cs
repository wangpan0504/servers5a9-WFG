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
    // 额外事件沿用 2037 的已验证记录形状（u32 1、两空串、标志 0）；需要活动状态的事件另附数值块。
    public static byte[] EventInfoCompressed(IReadOnlyList<ushort>? extraEvents = null,
        IReadOnlyList<ushort>? activeEvents = null, IReadOnlyList<EventInfoDesignPage>? designPages = null,
        IReadOnlyDictionary<ushort, uint>? firstValues = null, bool includeSeriaLuck = true,
        IReadOnlyDictionary<ushort, IReadOnlyList<uint>>? eventValues = null)
    {
        extraEvents ??= [];
        activeEvents ??= [];
        designPages ??= [];
        if (extraEvents.Any(id => id is 0 or 9 or SeriaLuckEventId) || extraEvents.Distinct().Count() != extraEvents.Count)
            throw new InvalidDataException("EVENT_INFO 附加事件无效");
        if (activeEvents.Count >= byte.MaxValue || activeEvents.Any(id => !extraEvents.Contains(id) && !designPages.Any(page => page.EventId == id)) ||
            activeEvents.Distinct().Count() != activeEvents.Count)
            throw new InvalidDataException("EVENT_INFO 活动状态必须唯一、存在于附加事件集合且不超过254项");
        if (firstValues is not null && firstValues.Keys.Any(id => !activeEvents.Contains(id)))
            throw new InvalidDataException("EVENT_INFO 首值必须属于活动状态集合");
        if (eventValues is not null && eventValues.Any(row => !activeEvents.Contains(row.Key) || row.Value.Count != 12))
            throw new InvalidDataException("EVENT_INFO 数值块必须属于活动状态集合且包含12项");
        var directoryIds = extraEvents.Concat(designPages.Select(page => page.EventId)).ToArray();
        if (directoryIds.Any(id => id is 0 or 9 or SeriaLuckEventId) ||
            directoryIds.Distinct().Count() != directoryIds.Length || directoryIds.Length >= ushort.MaxValue)
            throw new InvalidDataException("EVENT_INFO页面必须使用唯一非保留ID且总行数不超过u16");
        using var plain = new MemoryStream();
        using (var writer = new BinaryWriter(plain, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(checked((ushort)((includeSeriaLuck ? 1 : 0) + extraEvents.Count + designPages.Count)));
            if (includeSeriaLuck)
            {
            writer.Write(SeriaLuckEventId);
            writer.Write(1u);
            writer.Write(0);
            writer.Write(0);
            writer.Write((byte)0);
            }
            foreach (ushort id in extraEvents)
            {
                writer.Write(id);
                writer.Write(1u);
                writer.Write(0);
                writer.Write(0);
                writer.Write((byte)0);
            }
            foreach (var page in designPages)
            {
                writer.Write(page.EventId);
                writer.Write(page.MetadataValue);
                PacketEncoding.WriteString(writer, page.MetadataText1);
                PacketEncoding.WriteString(writer, page.MetadataText2);
                writer.Write((byte)1);
                writer.Write(page.Record04);
                writer.Write(page.Record08);
                PacketEncoding.WriteString(writer, page.Title);
                PacketEncoding.WriteString(writer, page.ShortName);
                PacketEncoding.WriteString(writer, page.Link);
                writer.Write(page.Record4C);
                writer.Write(page.Record50);
                PacketEncoding.WriteString(writer, page.Route);
                PacketEncoding.WriteString(writer, CalendarContent(page));
                writer.Write((byte)(page.Visible ? 1 : 0));
            }
            writer.Write((byte)((includeSeriaLuck ? 1 : 0) + activeEvents.Count));
            if (includeSeriaLuck)
            {
            writer.Write(SeriaLuckEventId);
            writer.Write((uint)SeriaLuckValueMax);
            for (int index = 1; index < 12; index++)
                writer.Write(0u);
            }
            foreach (ushort id in activeEvents)
            {
                writer.Write(id);
                if (eventValues is not null && eventValues.TryGetValue(id, out var values))
                {
                    foreach (uint value in values) writer.Write(value);
                }
                else
                {
                    writer.Write(firstValues is not null && firstValues.TryGetValue(id, out uint value) ? value : 0u);
                    for (int index = 1; index < 12; index++) writer.Write(0u);
                }
            }
        }
        plain.Position = 0;
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            plain.CopyTo(zlib);
        return compressed.ToArray();
    }

    static string CalendarContent(EventInfoDesignPage page)
    {
        if (page.ShowItemIds.Count == 0) return page.Content;
        if (page.ShowItemIds.Any(id => id <= 0) || page.ShowItemIds.Distinct().Count() != page.ShowItemIds.Count)
            throw new InvalidDataException("活动日历展示道具必须使用唯一正ID");
        // 026BFE50从扩展页Content读取原生登记标签、条数及模板ID。
        return page.Content + "\n[show_item_index] " + page.ShowItemIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " " + string.Join(" ", page.ShowItemIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    static void ValidateStage(int stage)
    {
        if (stage is < 0 or > SeriaLuckValueMax)
            throw new InvalidDataException($"无效赛丽亚幸运阶段: {stage}");
    }
}
