using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Game.Party;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarPacketTests
{
    public static void RunCore()
    {
        int before = Count;
        That(EvildomWarRequests.IsStart([]) && !EvildomWarRequests.IsStart([0]) &&
            !EvildomWarRequests.IsStart([1, 0, 0, 0]), "1652空正文，不能接受伪造模式或填充");
        Bytes([1], EvildomWarPackets.Start(true), "1652成功无附加字段");
        Bytes([0, 0, 0], EvildomWarPackets.Start(false), "1652失败含公共u16，专用消费者忽略其值并统一显示68129");
        Bytes(Convert.FromHexString("7856341202000200FFFFFFFF000000000203000080EFCDAB89FF00"),
            EvildomWarPackets.Rewards(0x12345678, [new(0, [new(0, -1, 0), new(2, int.MinValue + 3, 0x89ABCDEF)]), new(255, [])]),
            "1673独立金样保留原首字段、负值、u32高位和空组，不混同配置五列或限制为已猜物品类型");
        Bytes([0, 0, 0, 0, 0], EvildomWarPackets.Rewards(0, []), "1673空列表仍有完整首字段和组数");
        Bytes([12, 235, 3, 0, 0], EvildomWarPackets.Rewards(256780, []),
            "1673首字段为256780毫秒，供客户端拆分为04分16秒78，不先换成秒或百分秒");
        Bytes(Convert.FromHexString("00000000010203000403020105000000021413121116000000032423222126000000"),
            EvildomWarPackets.Rewards(0, [new(2, [new(0, 0x01020304, 5), new(2, 0x11121314, 22), new(3, 0x21222324, 38)])]),
            "1673非零槽位2的三个卡种0/2/3保持在同一成员组，不能把卡种拆成成员槽位");
        That(EvildomWarPackets.Rewards(0, [new(7, new EvildomWarPackets.RewardRow[255])]) is { Length: 2302 } maximum &&
            maximum[6] == 255, "1673单组u8最大行数不截断");
        Reject<ArgumentOutOfRangeException>(() => EvildomWarPackets.Rewards(0, new EvildomWarPackets.RewardGroup[256]),
            "1673组数溢出不能截成零");
        Reject<ArgumentOutOfRangeException>(() => EvildomWarPackets.Rewards(0, [new(0, new EvildomWarPackets.RewardRow[256])]),
            "1673行数溢出不能截断包体");
        Bytes(Convert.FromHexString("0201B80B000002000000321500000002002E150000010003"),
            EvildomWarPackets.Info(1, false, 3000, [new(5426, 0, 2, 0), new(5422, 1, 0, 3)]),
            "1671按真实读宽投影状态、模式、秒数、记录数、逐图三字节，不混用拼图NOTI1652");
        Bytes(Convert.FromHexString("0301B80B000002000000321500000002002E150000010003"),
            EvildomWarPackets.Info(1, true, 3000, [new(5426, 0, 2, 0), new(5422, 1, 0, 3)]),
            "1671战役完成仅将首状态改3，保留真实剩余时间和逐图记录，不用清零倒计时代替收尾");
        for (ushort mode = 0; mode < 3; mode++)
        {
            var settings = PartySettings.Default with { TitleCode = 4, Kind = 5, KindArgument = mode,
                Capacity = mode == 2 ? (byte)1 : (byte)4 };
            byte[] party = PartyPackets.Information(1, 2, 41, [(0, 42)], 0, false, settings);
            That(party.Length == 56 && party[23] == 5 && BitConverter.ToUInt16(party, 24) == mode &&
                party[54] == 5 && party[55] == 0,
                "芮兹菜单使用最终安装的队伍类型，NOTI9包尾不能把设置段的魔界类型5覆盖为0: " + mode);
            byte[] members = PartyPackets.Members(1, 2, 41, [(0, 42)], 0, settings);
            That(members.Length == 30 && members[9] == 2 && members[28] == 5 && members[29] == 0,
                "成员增量不附加设置或模式字段，末尾仍保留魔界类型5: " + mode);
        }
        Console.WriteLine($"PASS evildom war packets {Count - before}");
    }
}
