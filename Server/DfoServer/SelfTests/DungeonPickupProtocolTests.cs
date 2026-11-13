using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Game.Currency;

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
        // 实机地面key 15185 的客户端短文：key:u16 + form=1 + context + 7×u16，尾段与长式同布局但正文只有18B。
        // 尾段字节必须与长式逐字节一致（同为小端u16），否则断言实际在验证另一种字节序。
        byte[] shortBody = Convert.FromHexString("513B01042381349245A356B467C578D689E7");
        That(DungeonPickupRequests.TryParseOrdinary(shortBody, out var shortRequest), "识别普通18B短式请求");
        That(shortRequest == new DungeonPickupRequest(0x3B51, 0x04, 0x8123, 0x9234, 0xA345,
            0xB456, 0xC567, 0xD678, 0xE789), "短式与长式保存同一组坐标和token，读数严格不越过18B");
        foreach (int length in Enumerable.Range(0, 20).Where(length => length != 18).Concat(new[] { 21, 22, 24, 40 }))
            That(!DungeonPickupRequests.TryParseOrdinary(new byte[length], out var invalid) && invalid == default,
                "拒绝截尾或追加字节长度" + length);
        foreach (byte fixedField in new byte[] { 1, 4, 255 })
        {
            byte[] invalidBody = new byte[20];
            invalidBody[4] = fixedField;
            That(!DungeonPickupRequests.TryParseOrdinary(invalidBody, out var invalid) && invalid == default,
                "普通固定字段非零拒绝" + fixedField);
        }
        foreach (byte form in new byte[] { 0, 2, 255 })
        {
            byte[] invalidBody = new byte[18];
            invalidBody[2] = form;
            That(!DungeonPickupRequests.TryParseOrdinary(invalidBody, out var invalid) && invalid == default,
                "短式form非1拒绝" + form);
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
        var gold = DungeonPickupPackets.SinglePlayerGoldNotification(0xFEDCBA98, 0x8123, 123456);
        using (var stream = new MemoryStream(gold))
        using (var reader = new BinaryReader(stream))
        {
            That(gold.Length == 55 && reader.ReadUInt32() == 0xFEDCBA98 && reader.ReadUInt16() == 0x8123,
                "金币通知沿本版u32地面key和u16拾取者头部，不使用A21的117B布局");
            for (int member = 0; member < 8; member++)
            {
                bool participates = reader.ReadByte() != 0;
                uint amount = reader.ReadUInt32();
                That(participates == (member == 0) && amount == (member == 0 ? 123456u : 0u),
                    "金币消费者每个成员均读取金额，非本人成员也不省略四字节");
                if (participates)
                    That(reader.ReadByte() == 1 && reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0,
                        "单人显式保留零附加金币及零公会基金，让金币消费者进入含地面对象的扩展表现分支");
            }
            That(stream.Position == stream.Length, "八成员金币通知完整消费，无普通物品目的槽或尾部");
        }
        Reject<ArgumentOutOfRangeException>(() => DungeonPickupPackets.SinglePlayerGoldNotification(1, 2, 0),
            "未产生已提交金币增量不得编码金币拾取成功");
        var bonusGold = DungeonPickupPackets.IndependentGoldNotification(1, 2, 154, 3, 53);
        That(bonusGold.Length == 55 && bonusGold.AsSpan(6, 15).ToArray().All(value => value == 0) &&
            bonusGold.AsSpan(21, 14).SequenceEqual(new byte[] { 1, 154, 0, 0, 0, 1, 53, 0, 0, 0, 0, 0, 0, 0 }) &&
            bonusGold.AsSpan(35).ToArray().All(value => value == 0),
            "金币原生扩展只向真实成员写总额及已含在总额内的装备附加额，基金与其余成员保持零");
        Reject<ArgumentOutOfRangeException>(() => DungeonPickupPackets.SinglePlayerGoldNotification(1, 2, 10, 11),
            "附加展示金额不能超过实际到账总额");
        var singleGold = DungeonPickupPackets.SinglePlayerGoldNotification(uint.MaxValue, ushort.MaxValue, 1);
        That(singleGold.AsSpan(0, 12).SequenceEqual(new byte[] { 255, 255, 255, 255, 255, 255, 1, 1, 0, 0, 0, 1 }) &&
            singleGold.AsSpan(12).ToArray().All(value => value == 0),
            "最小金币增量保留完整key/拾取者及扩展开关，不伪造奖金、基金或其他成员资产");
        That(CurrencyPackets.PickupSlot(CurrencyKind.Gold) == 0 && CurrencyPackets.PickupSlot(CurrencyKind.ClearCube) == 358 &&
            CurrencyPackets.PickupSlot(CurrencyKind.GoldCube) == 359 && CurrencyPackets.PickupSlot(CurrencyKind.EpicSoul) == 365,
            "集合材料投影使用本版虚拟余额槽，不把账号材料写入角色金币槽");
        Reject<NotSupportedException>(() => CurrencyPackets.PickupSlot(CurrencyKind.Cera),
            "没有普通拾取槽的货币不得猜测槽0发布");
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
