using static DfoServer.Testing.TestCheck;
using System.IO.Compression;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Channels;
using DfoServer.Network.Builders;

namespace DfoServer;

// GM 团本时段 → EVENT_INFO 事件集合、RAID_WEEKLY_CLEAR_INFO 次数表，以及频道 39 改回 type 45 的静态断言（非实机）。
internal static class RaidEntryEventsTests
{
    public static void Run()
    {
        int before = Count;
        That(RaidEntryEvents.Cards.Select(card => (card.Name, card.EventId, card.RaidType, card.Quest)).SequenceEqual(new[]
            { ("luke", (ushort)282, (byte)1, 2903), ("hyperspace", (ushort)2449, (byte)2, 8672), ("prey", (ushort)414, (byte)3, 8935) }),
            "三张卡按 01711690 对应事件 282/2449/414、团本类型 1/2/3、任务 2903/8672/8935");

        byte[] plain = Inflate(SeriaLuckPackets.EventInfoCompressed());
        That(plain.Length == 68, "无团本时段时 EVENT_INFO 与原赛丽亚 68B 正文一致");
        byte[] withRaid = Inflate(SeriaLuckPackets.EventInfoCompressed([2449, 282]));
        byte[] expected = Build(writer =>
        {
            writer.Write((ushort)3);
            foreach (ushort id in new ushort[] { 2037, 2449, 282 })
            {
                writer.Write(id); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
            }
            writer.Write((byte)1); writer.Write((ushort)2037); writer.Write(8u);
            for (int index = 1; index < 12; index++) writer.Write(0u);
        });
        That(withRaid.AsSpan().SequenceEqual(expected), "EVENT_INFO 在 2037 之后逐个追加团本事件记录，数值块仍只有 2037");
        byte[] withJonathan = Inflate(SeriaLuckPackets.EventInfoCompressed([2378, 2395, 2449, 282], [2395]));
        byte[] expectedJonathan = Build(writer =>
        {
            writer.Write((ushort)5);
            foreach (ushort id in new ushort[] { 2037, 2378, 2395, 2449, 282 })
            {
                writer.Write(id); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0);
            }
            writer.Write((byte)2); writer.Write((ushort)2037); writer.Write(8u);
            for (int index = 1; index < 12; index++) writer.Write(0u);
            writer.Write((ushort)2395);
            for (int index = 0; index < 12; index++) writer.Write(0u);
        });
        Bytes(withJonathan, expectedJonathan, "乔纳森2395同时有目录和活动数值块，赛丽亚、红包及团本原记录不变");
        That(Throws(() => SeriaLuckPackets.EventInfoCompressed([2378], [2395])) &&
            Throws(() => SeriaLuckPackets.EventInfoCompressed([2395], [2395, 2395])), "拒绝目录外活动状态和重复活动状态");
        ushort[] tooManyActive = Enumerable.Range(3000, 255).Select(id => (ushort)id).ToArray();
        That(Throws(() => SeriaLuckPackets.EventInfoCompressed(tooManyActive, tooManyActive)), "活动状态含赛丽亚后不能超过u8计数上限");
        That(Throws(() => SeriaLuckPackets.EventInfoCompressed([9])) && Throws(() => SeriaLuckPackets.EventInfoCompressed([2037])) &&
            Throws(() => SeriaLuckPackets.EventInfoCompressed([282, 282])), "拒绝事件 9（改变 026BE900 读宽）、重复 2037 与重复事件");

        byte[] counts = RaidPackets.WeeklyClearInfo([new(1, 0, 0), new(2, 0, 1), new(3, 1, 2)]);
        That(counts.AsSpan().SequenceEqual(new byte[] { 3, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 3, 1, 0, 0, 0, 2, 0, 0, 0, 0 }),
            "RAID_WEEKLY_CLEAR_INFO：u8 行数 + {u8 类型, u32 日已用, u32 周已用, u8 0}");
        That(Throws(() => RaidPackets.WeeklyClearInfo([new(1, 0, 0), new(1, 0, 0)])), "同一团本类型不重复写");

        DateTimeOffset tuesday = new(2026, 10, 20, 19, 0, 0, RaidEntrySchedule.Zone);
        string directory = Path.Combine(Path.GetTempPath(), "raid-events-" + Guid.NewGuid().ToString("N"));
        var store = new RaidEntryStore(directory);
        That(RaidEntryEvents.OpenEvents(null, tuesday).Length == 0 && RaidEntryEvents.OpenEvents(store, tuesday).Length == 0, "没有时段表时不下发团本事件");
        store.Replace("""{"luke":[],"hyperspace":[{"weekday":2,"start":"18:00","end":"22:00"}],"prey":[{"weekday":3,"start":"00:00","end":"24:00"}]}""");
        That(RaidEntryEvents.OpenEvents(store, tuesday).SequenceEqual(new ushort[] { 2449 }), "周二 19:00 只开超时空漩涡 → 事件 2449");
        That(RaidEntryEvents.OpenEvents(store, tuesday.AddHours(3)).Length == 0, "周二 22:00 结束时刻不含");
        That(RaidEntryEvents.OpenEvents(store, tuesday.AddHours(6)).SequenceEqual(new ushort[] { 414 }), "周三 01:00 只开普雷 → 事件 414");
        That(RaidEntryEvents.ChannelEvents.Select(row => (row.Name, row.EventId, row.Quest)).SequenceEqual(new[] { ("anton", (ushort)181, 12829) }) &&
            RaidEntrySchedule.Names.Contains("anton") && RaidEntryEvents.Cards.Length == 3, "安徒恩频道事件 181/任务 12829 单列，不新增卡");
        That(!store.IsOpen("anton", tuesday), "旧时段文件没有 anton 键时安徒恩关闭");
        store.Replace("""{"luke":[],"hyperspace":[{"weekday":2,"start":"18:00","end":"22:00"}],"prey":[],"anton":[{"weekday":2,"start":"00:00","end":"24:00"}]}""");
        That(RaidEntryEvents.OpenEvents(store, tuesday).SequenceEqual(new ushort[] { 2449, 181 }), "周二 19:00 开超时空漩涡与安徒恩 → 事件 2449、181");
        That(new RaidEntryStore(directory).IsOpen("anton", tuesday), "anton 时段写盘后重开目录可读回");
        That(Inflate(SeriaLuckPackets.EventInfoCompressed(RaidEntryEvents.OpenEvents(store, tuesday))).Length == 68 + 2 * 15, "EVENT_INFO 追加 181 记录");
        TestFiles.DeleteDirectory(directory, true);

        That(ChannelAreaLanding.FiendwarType == 45, "超时空之战频道类型是 CHANNEL_FIEND_WAR 0x2D");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-raid-channel-" + Guid.NewGuid().ToString("N"));
        var channels = Infrastructure.ServerRuntime.LoadChannels(Path.Combine(root, "channel.txt"));
        var fiend = channels.Channels.Where(row => row.Number == 39).ToArray();
        That(fiend.Length == 1 && fiend[0].Type == 45 && fiend[0].Group == "[fiendwar]" && fiend[0].NameReference == "<chn_channel_info_048>",
            "内置 channel.txt 频道 39 [fiendwar] 写 type 45");
        That(channels.Channels.All(row => row.Type != 42) && channels.Entry(39).Type == ChannelAreaLanding.FiendwarType, "不再有 type 42 频道，入口类型与落点常量一致");
        if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true);
        Console.WriteLine($"PASS {Count - before} 团本时段事件、次数表与超时空漩涡 type 45 断言（非实机）");
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    static byte[] Inflate(byte[] body)
    {
        using var compressed = new MemoryStream(body, writable: false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) write(writer);
        return stream.ToArray();
    }
}
