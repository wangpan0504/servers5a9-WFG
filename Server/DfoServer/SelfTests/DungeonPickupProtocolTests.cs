using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class DungeonPickupProtocolTests
{
    public static void Run()
    {
        int checks = Count;
        // 独立固定字节覆盖完整u32 key、未知byte和全部高位u16；不是实机拾取或资产资格样本。
        byte[] requestBody = Convert.FromHexString("98BADCFE00A52381349245A356B467C578D689E7");
        That(DungeonPickupRequests.TryParseOrdinary(requestBody, out var request), "识别普通20B请求");
        That(request == new DungeonPickupRequest(0xFEDCBA98, 0xA5, 0x8123, 0x9234, 0xA345,
            0xB456, 0xC567, 0xD678, 0xE789), "完整保存地面key、未知context、坐标和三个token");
        requestBody[5] = 0;
        requestBody[18] = 0;
        That(request.Context == 0xA5 && request.Token2 == 0xE789, "解析值不借用可变输入");
        foreach (int length in Enumerable.Range(0, 20).Concat(new[] { 21, 22, 24, 40 }))
            That(!DungeonPickupRequests.TryParseOrdinary(new byte[length], out var invalid) && invalid == default,
                "拒绝截尾、18B变体或追加字节长度" + length);
        foreach (byte fixedField in new byte[] { 1, 4, 255 })
        {
            byte[] invalidBody = new byte[20];
            invalidBody[4] = fixedField;
            That(!DungeonPickupRequests.TryParseOrdinary(invalidBody, out var invalid) && invalid == default,
                "普通固定字段非零拒绝" + fixedField);
        }
        byte[] maximumRequest = Enumerable.Repeat(byte.MaxValue, 20).ToArray();
        maximumRequest[4] = 0;
        That(DungeonPickupRequests.TryParseOrdinary(maximumRequest, out var maximum)
            && maximum == new DungeonPickupRequest(uint.MaxValue, byte.MaxValue, ushort.MaxValue, ushort.MaxValue,
                ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue),
            "未知字段保留完整无符号上界，不缩窄为布尔或资产值");
        That(DungeonPickupRequests.TryParseOrdinary(new byte[20], out var zero) && zero == default,
            "codec不猜测零key、坐标或token的业务合法性");

        byte[] flags = [0, 1, 2, 3, 128, 253, 254, 255];
        byte[] notice = DungeonPickupPackets.OrdinaryNotification(0xFEDCBA98, 0x8123, flags, 0x9234, 0xA345, 0xE7);
        That(notice.SequenceEqual(Convert.FromHexString("98BADCFE23810001020380FDFEFF349245A3E7")),
            "普通NOTI39精确19B，8字节flags和尾byte不截断或补充核心");
        using (var stream = new MemoryStream(notice))
        using (var reader = new BinaryReader(stream))
        {
            That(reader.ReadUInt32() == 0xFEDCBA98 && reader.ReadUInt16() == 0x8123,
                "独立读器消费完整groundKey与首picker");
            That(reader.ReadBytes(8).SequenceEqual(flags) && reader.ReadUInt16() == 0x9234
                && reader.ReadUInt16() == 0xA345 && reader.ReadByte() == 0xE7 && stream.Position == stream.Length,
                "独立读器消费完整flags、recipient、实际slot和tail，不留额外正文");
        }
        flags[0] = 255;
        That(notice[6] == 0, "通知复制显式flags，不保留调用方缓冲引用");
        foreach (int length in new[] { 0, 7, 9 })
            Reject<ArgumentException>(() => DungeonPickupPackets.OrdinaryNotification(1, 2, new byte[length], 3, 4, 5),
                "拒绝不完整flags长度" + length);
        That(DungeonPickupPackets.OrdinaryNotification(uint.MaxValue, ushort.MaxValue,
            Enumerable.Repeat(byte.MaxValue, 8).ToArray(), ushort.MaxValue, ushort.MaxValue, byte.MaxValue)
            .All(value => value == byte.MaxValue), "通知完整保存所有无符号上界");
        That(DungeonPickupPackets.OrdinaryNotification(0, 0, new byte[8], 0, 0, 0).SequenceEqual(new byte[19]),
            "显式零flags可编码，Builder不推断特殊资源或奖励资格");

        That(DungeonPickupPackets.Accepted().SequenceEqual(new byte[] { 1 }), "ACK43成功精确1B");
        foreach (ushort error in new ushort[] { 0, 1, 0x8123, ushort.MaxValue })
        {
            byte[] rejection = DungeonPickupPackets.Rejected(error);
            using var stream = new MemoryStream(rejection);
            using var reader = new BinaryReader(stream);
            That(rejection.Length == 3 && reader.ReadByte() == 0 && reader.ReadUInt16() == error
                && stream.Position == stream.Length, "ACK43拒绝精确公共0+完整u16错误" + error);
        }
        Console.WriteLine($"PASS {Count - checks} 普通副本拾取协议断言");

    }
}
