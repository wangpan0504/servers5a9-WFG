using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019WireTests
{
    public static void RunCore()
    {
        int before = Count;
        RunAccountData();
        MatchingRequestsAndAcknowledgements();
        // 006C0EB0/006C1750只初始化前两个u32，第三字段保留为原值，业务不得用于选择技能目标或效果。
        var request = Strongway2019Requests.Symbol(Convert.FromHexString("0100000004000000EFBEADDE"));
        That(request == new Strongway2019SymbolRequest(1, 4, 0xDEADBEEF), "1525读取完整12B；栈残值不能被误解为合法效果参数");
        foreach (int length in new[] { 0, 8, 11, 13 })
            Reject<InvalidDataException>(() => Strongway2019Requests.Symbol(new byte[length]), "1525拒绝截断或额外字段");
        That(Strongway2019Packets.Symbol(1, 4, 400596).SequenceEqual(Convert.FromHexString("010100000004000000D41C0600")),
            "1525成功应答为成功u8及side/kind/value三个u32，不能省略前缀或改用通知类别");
        That(Strongway2019Packets.Result(1, 2).SequenceEqual(new byte[] { 1, 2 }),
            "1494只含win和condition两个u8，不添加通用成功前缀");
        That(Strongway2019Packets.Waiting(0x12345678, 0xAABBCCDD, 1,
            [0x1234, 0x5678, 0x9ABC, 0xDEF0, 0x1111, 0x2222, 0x3333, 0x4444]).SequenceEqual(
            Convert.FromHexString("78563412DDCCBBAA010834127856BC9AF0DE1111222233334444")),
            "1492两个u32、阵营u8、数量u8及8个u16角色标识的独立金样");
        Reject<InvalidDataException>(() => Strongway2019Packets.Waiting(0, 0, 1, [1, 1]), "等待名单不重复标识");
        Reject<InvalidDataException>(() => Strongway2019Packets.Waiting(0, 0, 2, [1, 2]), "阵营仅0/1");
        var matching = PvpMatchingRequests.Match(Convert.FromHexString("010E000000FFFFFFFFFFFFFFFF00F0140000"));
        That(matching.Kind == 1 && matching.GameType == 14 && matching.TrailingValue == 5360 &&
            matching.Parameters.SequenceEqual(new byte[] { 0, 0, 0 }) && matching.Pairs.All(pair => pair.First == 255 && pair.Second == 255),
            "首期CMD520复用唯一18B解析，gameType14且副本5360，不借决斗选项解释FF占位");
        Console.WriteLine($"PASS {Count - before} 首期强者之路原生协议断言");
    }

    public static void RunAccountData()
    {
        int before = Count;
        That(Strongway2019ProfilePackets.UserData(new(0, 2), new(1, 1)).SequenceEqual(
            Convert.FromHexString("000000000000000000000000020000000000000001000000")),
            "1753精确六u32：四个原生默认RawReserved0，第四/第六为真实普通/匹配账号周已用次数，不写日次数、角色成绩或剩余额度");
        That(Strongway2019ProfilePackets.UserData(new(0, 0), new(0, 0)).SequenceEqual(new byte[24]),
            "1753空账号仍发送完整24B原生默认，不缩成两字段");
        Console.WriteLine($"PASS {Count - before} 首期1753账号周奖励精确包体");
    }

    static void MatchingRequestsAndAcknowledgements()
    {
        // 006C25A0原生writer独立金样；本期不得复用PK的职业排除解释。
        byte[] canonical = Convert.FromHexString("010E000000FFFFFFFFFFFFFFFF00F0140000");
        var matching = PvpMatchingRequests.Match(canonical);
        That(Strongway2019Requests.IsMatching(matching),
            "CMD520首期原生18B请求进入4v4业务路由，不再被当作不支持的PK模式");
        foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17 })
        {
            var changed = canonical.ToArray();
            changed[offset] ^= 1;
            That(!Strongway2019Requests.IsMatching(PvpMatchingRequests.Match(changed)),
                "首期匹配拒绝变更原生常量、FF占位或5360地下城，失败交业务收尾：" + offset);
        }
        That(!Strongway2019Requests.IsMatching(PvpMatchingRequests.Match(Convert.FromHexString("010E0000000000"))),
            "其他7B匹配布局不能冒充首期18B原生匹配请求");
        var cancel = Strongway2019Requests.MatchingControl(Convert.FromHexString("010E00"));
        That(cancel == new Strongway2019MatchingControlRequest(1, 14) && cancel.IsMatching,
            "CMD521取消和CMD522退出共享kind:u8及gameType:u16精确3B，不能误用空退出正文");
        var other = Strongway2019Requests.MatchingControl(Convert.FromHexString("123456"));
        That(other.Kind == 0x12 && other.GameType == 0x5634 && !other.IsMatching,
            "匹配控制保留原字段供业务拒绝，不把未知玩法静默改成14");
        foreach (int length in new[] { 0, 1, 2, 4, 18 })
            Reject<InvalidDataException>(() => Strongway2019Requests.MatchingControl(new byte[length]),
                "首期取消及退出拒绝截断、附加字段和误用开始请求");
        That(Strongway2019Packets.MatchingAccepted(0).SequenceEqual(Convert.FromHexString("01010E0000")) &&
            Strongway2019Packets.MatchingAccepted(0x7A).SequenceEqual(Convert.FromHexString("01010E007A")),
            "ACK520含成功u8、kind1、gameType14及原始flag共5B，未知flag不被改成倒计时");
        That(PvpMatchingPackets.Cancelled().SequenceEqual(new byte[] { 1 }) &&
            PvpMatchingPackets.Rejected(0x1234).SequenceEqual(Convert.FromHexString("003412")),
            "ACK521成功无附加字段；520/521失败使用公共u16错误，不伪造1491或522回执");
    }
}
