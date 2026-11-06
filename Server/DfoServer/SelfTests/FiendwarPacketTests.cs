using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Game.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarPacketTests
{
    // 独立按客户端600分类2读宽消费，供团队和引导TCP核对完整第二页。
    internal static Dictionary<ushort, byte[]> ReadTeamResults(byte[] body)
    {
        That(body.Length >= 2 && body[0] == 3 && body[1] is >= 1 and <= 8, "漩涡第二页为有界wire3完整团队表");
        var rows = new Dictionary<ushort, byte[]>();
        int at = 2;
        for (int index = 0; index < body[1]; index++)
        {
            That(body.Length >= at + 4 && body[at + 2] <= 3 && body[at + 3] > 0, "团队行保留owner、卡位和非空物品数量");
            ushort owner = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
            int size = 4 + 16 * body[at + 3];
            That(body.Length >= at + size, "团队行完整消费原生16B物品");
            That(rows.TryAdd(owner, body.AsSpan(at + 4, size - 4).ToArray()), "完整团队表不重复owner");
            at += size;
        }
        That(at == body.Length, "团队表无截断或尾部未消费字段");
        return rows;
    }

    internal static void RunCore()
    {
        int before = Count;
        Bytes(FiendwarPackets.State(1, 0x1234), [1, 0x34, 0x12], "漩涡团状态完整3B");
        var offer = new RaidRewardOffer(0, 0, [new(0x12345678, 3)], [])
        { PreyDisplay = [new(0, 0x12345678, 3, 0, 0, 0)] };
        Bytes(PreyRewardPackets.FiendwarTeamResults([(0x1234, offer)]),
            [3, 1, 0x34, 0x12, 0, 1, 0, 0x78, 0x56, 0x34, 0x12, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "漩涡第二页完整wire3、owner和16B产物的独立字节预期");
        Reject<InvalidDataException>(() => PreyRewardPackets.FiendwarTeamResults([(1, offer), (1, offer)]),
            "完整团队表拒绝重复owner");
        Reject<InvalidDataException>(() => PreyRewardPackets.FiendwarTeamResults([(1, offer with { Kind = 1 })]),
            "团队页不能混入本人固定奖励类型");
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
        foreach (byte lane in new byte[] { 0, 1, 2 })
        {
            That(FiendwarRequests.SupportLane([lane]) == lane, "1570的三个原生路线号均可解析");
            Bytes(FiendwarPackets.SupportNotice(lane), [lane], "NOTI1554只含路线号，不混入Boss操作或1530符号");
        }
        foreach (byte[] invalid in new byte[][] { [], [3], [255], [0, 0] })
            Reject<InvalidDataException>(() => FiendwarRequests.SupportLane(invalid), "支援请求拒绝缺失、越界和尾字节");
        Reject<ArgumentOutOfRangeException>(() => FiendwarPackets.SupportNotice(3), "支援通知不构造未知路线");
        Reject<ArgumentOutOfRangeException>(() => FiendwarPackets.Warnings(new (byte, byte)[256]), "禁止数量溢出截断");
        Reject<ArgumentOutOfRangeException>(() => FiendwarPackets.Users([new(3, 2, 1, 0)]), "未知成员操作拒绝");
        That(FiendwarRequests.Boss([2, 0, 0, 0, 4, 0, 0, 0]) == new FiendwarBossRequest(2, 4), "Boss请求2个u32");
        foreach (int length in new[] { 0, 4, 7, 9 })
            Reject<InvalidDataException>(() => FiendwarRequests.Boss(new byte[length]), "拒绝截断和多余字段");
        var counter = new byte[40];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(counter, FiendwarCounterRequests.Magic);
        foreach (int length in new[] { 0, 39, 41 })
            Reject<InvalidDataException>(() => FiendwarCounterRequests.Parse(new byte[length]), "破招私报拒绝截断和尾字节");
        That(!FiendwarCounterRequests.Parse(counter).Valid, "空房间和零序号不构造有效破招事件");
        Reject<ArgumentOutOfRangeException>(() => new DfoServer.Network.Protocol.GameCommandRegistry().RegisterPrivate(417,
            (_, _) => Task.FromResult(DfoServer.Network.Protocol.CommandResult.Handled), "fixture"), "扩展注册不允许劫持原生417");
        Console.WriteLine($"PASS {Count - before} 漩涡协议断言");
    }
}
