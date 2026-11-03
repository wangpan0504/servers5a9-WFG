using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomTimePointWireTests
{
    public static void RunCore()
    {
        int before = Count;
        That(PcRoomTimePointRequests.TryParse([0, 0xff], out var query) && query == new PcRoomTimePointRequest(0, 0xff),
            "在线领奖查询为00 FF");
        foreach (byte type in new byte[] { 1, 2, 4, 8 })
            That(PcRoomTimePointRequests.TryParse([type, 0xff], out var daily) && daily.Type == type && daily.Index == 0xff,
                "每日领取使用单档位与FF索引");
        for (byte index = 0; index <= 4; index++)
            That(PcRoomTimePointRequests.TryParse([0x10, index], out var period) && period.Type == 0x10 && period.Index == index,
                "累计领取保留客户端支持的五个索引，实际资源档数交领域校验");
        That(!PcRoomTimePointRequests.TryParse([], out _) && !PcRoomTimePointRequests.TryParse([0], out _) &&
            !PcRoomTimePointRequests.TryParse([0, 0xff, 0], out _) &&
            !PcRoomTimePointRequests.TryParse([.. new byte[13], 0, 0xff], out _),
            "当前V31正文严格2B，不将别版13B前缀加入请求");
        foreach (byte[] invalid in new byte[][] { [0, 0], [1, 0], [3, 0xff], [0x10, 5], [0x10, 0xff], [0x20, 0], [0xff, 0xff] })
            That(!PcRoomTimePointRequests.TryParse(invalid, out _), "查询、每日位和累计索引不接受混用或未知选择");
        byte[] progress = PcRoomTimePointPackets.Progress(0x11223344, 0x55667788, 0x0a, 0x99aabbcc, 0xddeeff01);
        That(progress.AsSpan().SequenceEqual(new byte[] {
            0x44, 0x33, 0x22, 0x11, 0x88, 0x77, 0x66, 0x55, 0x0a,
            0xcc, 0xbb, 0xaa, 0x99, 0x01, 0xff, 0xee, 0xdd }),
            "NOTI561固定17B完整金样，所有多字节字段使用小端且视觉组先于累计领取位");
        That(PcRoomTimePointPackets.Progress(0, 0, 0, 0, 0).AsSpan().SequenceEqual(new byte[17]),
            "首次在线状态完整保留17B零值");
        Console.WriteLine($"PASS pcroom timepoint wire assertions {Count - before}");
    }
}
