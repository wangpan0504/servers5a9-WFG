using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TradePacketTests
{
    public static void RunCore()
    {
        Bytes(TradePackets.Invite(1, 0x0001129c, 1792716988), Convert.FromHexString("0100019C120100BCB0DA6A"), "交易邀请包");
        Bytes(TradePackets.Accepted(2, 0x0001129c, 1792716992), Convert.FromHexString("0200019C12010000C0B0DA6A"), "交易接受包");
        Bytes(TradePackets.ResponseAck(1), Convert.FromHexString("0101000100"), "交易回应ACK");
        Bytes(TradePackets.State(1, 5), Convert.FromHexString("010005"), "交易锁定通知");
        Bytes(TradePackets.MoveAck(0, 9, 1, 4, 3), Convert.FromHexString("0100090001000000040300"), "放入物品ACK");
        Bytes(TradePackets.MoveAck(4, 3, 1, 0, 9), Convert.FromHexString("0104030001000000000900"), "取回物品ACK");
        Bytes(TradePackets.MoveAck(4, 0, 1111, 0, 0), Convert.FromHexString("0104000057040000000000"), "取回金币ACK");
        var item = TradePackets.ChangedItem(3, 27800, 0x047ddcef, 45);
        That(item.Length == 123 && item.AsSpan(0, 12).SequenceEqual(Convert.FromHexString("0300986C0000EFDC7D04002D")) &&
            !item.AsSpan(12).ContainsAnyExcept((byte)0), "交易物品展示行保留大端耐久和固定宽度");
        var gold = TradePackets.ChangedGold(100);
        That(gold.Length == 123 && gold.AsSpan(0, 10).SequenceEqual(Convert.FromHexString("00000000000064000000")) &&
            !gold.AsSpan(10).ContainsAnyExcept((byte)0), "交易金币行编码金额");
        var removed = TradePackets.ClearedRow(3);
        That(removed.Length == 123 && removed.AsSpan(0, 6).SequenceEqual(Convert.FromHexString("0300FFFFFFFF")) &&
            !removed.AsSpan(6).ContainsAnyExcept((byte)0), "取回物品清除对方行");
        Bytes(TradePackets.Finish([(3, 9), (4, 10)]), Convert.FromHexString("02000300090004000A00"), "成交行目标槽");
        Bytes(TradePackets.Close(), [0, 0], "取消通知不含成交行");
    }
}
