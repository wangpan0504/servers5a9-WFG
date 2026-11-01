using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassPacketTests
{
    public static void RunCore()
    {
        int before = Count;
        Bytes(BattlePassPackets.PassInfo(0xA5, [0x12, 0x34, 0x56], 0x89ABCDEF, 0x01234567),
            Convert.FromHexString("A5123456EFCDAB8967452301"),
            "1800固定12B，保留type、三个原字节及两个独立小端u32偏移");
        Bytes(BattlePassPackets.PassInfo(0xFF, [0xFE, 0x80, 0x7F], uint.MaxValue, uint.MaxValue),
            Convert.FromHexString("FFFE807FFFFFFFFFFFFFFFFF"),
            "1800不猜type枚举，不清保留高位，不截断level或exp");
        Bytes(BattlePassPackets.PassInfo(0, new byte[3], 0, 0), new byte[12],
            "1800显式全零原值保持，不替调用者初始化等级");
        byte[] reserved = [0x12, 0x34, 0x56];
        byte[] pass = BattlePassPackets.PassInfo(0xA5, reserved, 0x89ABCDEF, 0x01234567);
        Array.Fill(reserved, (byte)0xCC);
        Bytes(pass, Convert.FromHexString("A5123456EFCDAB8967452301"), "1800结果拥有保留字节快照");
        foreach (int length in new[] { 0, 2, 4 })
            Reject<ArgumentException>(() => BattlePassPackets.PassInfo(0, new byte[length], 0, 0),
                "1800保留区必须显式给足3B，非法长度: " + length);

        BattlePassMissionCell[] cells =
        [
            new(0x0000, 0xFFFF, 0x00, 0x01),
            new(0xFFFF, 0x0000, 0x01, 0x00),
            new(0x1234, 0xABCD, 0x80, 0xFE),
            new(0x00FF, 0xFF00, 0xFF, 0x02),
            new(0x8000, 0x7FFF, 0x02, 0x80),
            new(0x4567, 0x89AB, 0x7F, 0xFF),
            new(0xFEDC, 0x3210, 0x01, 0x01),
            new(0x0080, 0x8001, 0x55, 0xAA)
        ];
        // 独立手写原字节及完整期望，不用被测Builder或同源编码函数生成预期。
        byte[] tail = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F" +
            "101112131415161718191A1B1C1D1E1F" +
            "202122232425262728292A2B2C2D2E2F" +
            "303132333435363738393A3B3C3D3E3F" +
            "404142434445464748494A4B4C4D4E4F" +
            "505152535455565758595A5B5C5D5E5F" +
            "606162636465666768696A6B");
        byte[] expected = Convert.FromHexString(
            "0000FFFF0001FFFF000001003412CDAB80FEFF0000FFFF02" +
            "0080FF7F02806745AB897FFFDCFE103201018000018055AA" +
            "000102030405060708090A0B0C0D0E0F" +
            "101112131415161718191A1B1C1D1E1F" +
            "202122232425262728292A2B2C2D2E2F" +
            "303132333435363738393A3B3C3D3E3F" +
            "404142434445464748494A4B4C4D4E4F" +
            "505152535455565758595A5B5C5D5E5F" +
            "606162636465666768696A6B");
        byte[] missions = BattlePassPackets.Missions(cells, tail);
        Bytes(missions, expected, "1801固定156B，前8槽保key/count/两个独立byte及顺序，后108B显式原样");
        Array.Clear(cells);
        Array.Fill(tail, (byte)0xCC);
        Bytes(missions, expected, "1801结果拥有任务槽和尾部快照，不借用调用者可变数组");
        Bytes(BattlePassPackets.Missions(new BattlePassMissionCell[8], new byte[108]), new byte[156],
            "1801允许调用者显式提供全零原字节，不把状态推断为业务完成");
        foreach (int length in new[] { 0, 7, 9 })
            Reject<ArgumentException>(() => BattlePassPackets.Missions(new BattlePassMissionCell[length], new byte[108]),
                "1801必须显式给8个6B槽，非法数量: " + length);
        foreach (int length in new[] { 0, 107, 109 })
            Reject<ArgumentException>(() => BattlePassPackets.Missions(new BattlePassMissionCell[8], new byte[length]),
                "1801必须显式给尾108B，非法长度: " + length);
        Console.WriteLine($"PASS battle pass packets {Count - before}");
    }
}
