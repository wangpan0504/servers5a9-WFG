using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarPacketTests
{
    internal static void RunCore()
    {
        int before = Count;
        Bytes(FiendwarPackets.State(1, 0x1234), [1, 0x34, 0x12], "漩涡团状态完整3B");
        Bytes(FiendwarPackets.Warnings([(2, 1), (3, 0)]), [2, 2, 1, 3, 0], "警告每行2B");
        Bytes(FiendwarPackets.Users([new(1, 6, 0x1234, 0)]),
            [1, 1, 0, 0, 0, 6, 0x34, 0x12, 0, 0, 0, 0, 0, 0], "角色ID按u32写入，取消罚站尾列为0");
        Bytes(FiendwarPackets.Monsters([new(7, 0x12345678, 2, 1)]),
            [1, 7, 0x78, 0x56, 0x34, 0x12, 2, 1, 0, 0, 0], "中立怪每行10B");
        Bytes(FiendwarPackets.Enemies([new(7, 0x12345678, 2, 2)]),
            [1, 7, 0x78, 0x56, 0x34, 0x12, 2, 2, 0, 0, 0, 2, 0, 0, 0], "军团每行14B，不漏尾列");
        Bytes(FiendwarPackets.Timers([new(8, 12, 1000, 600)]),
            [1, 8, 12, 0xe8, 3, 0, 0, 0x58, 2, 0, 0], "timer10B精确偏移");
        Bytes(FiendwarPackets.Merchant(6, 120), [6, 120, 0, 0, 0], "商人停留秒数不能漏发");
        Bytes(FiendwarPackets.Symbols([(81, 1)]), [1, 81, 0, 0, 0, 1, 0, 0, 0], "符号行8B");
        Reject<ArgumentOutOfRangeException>(() => FiendwarPackets.Warnings(new (byte, byte)[256]), "禁止数量溢出截断");
        Reject<ArgumentOutOfRangeException>(() => FiendwarPackets.Users([new(3, 2, 1, 0)]), "未知成员操作拒绝");
        That(FiendwarRequests.Boss([2, 0, 0, 0, 4, 0, 0, 0]) == new FiendwarBossRequest(2, 4), "Boss请求2个u32");
        foreach (int length in new[] { 0, 4, 7, 9 })
            Reject<InvalidDataException>(() => FiendwarRequests.Boss(new byte[length]), "拒绝截断和多余字段");
        Console.WriteLine($"PASS {Count - before} 漩涡协议断言");
    }
}
