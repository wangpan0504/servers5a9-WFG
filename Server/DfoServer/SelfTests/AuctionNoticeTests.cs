using static DfoServer.Testing.TestCheck;
using DfoServer.Network.Builders;

namespace DfoServer;

// 拍卖行尚未实现玩法；这里只锁定已确认的 183 包体字节，不表达任何拍卖行业务语义。
internal static class AuctionNoticeTests
{
    public static void Run()
    {
        int checks = Count;
        Bytes(AuctionPackets.ServiceNotice(AuctionPackets.ServiceTypeZero), [0x00, 0x01], "183 serviceType0 body");
        Bytes(AuctionPackets.ServiceNotice(AuctionPackets.ServiceTypeOne), [0x01, 0x01], "183 serviceType1 body");
        Reject<InvalidDataException>(() => AuctionPackets.ServiceNotice(0x02), "183 未确认的 serviceType 拒绝");
        Console.WriteLine($"PASS {Count - checks} auction service notice assertions");
    }
}
