using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpMatchingWireTests
{
    public static void RunCore()
    {
        int before = Count;
        That(PvpMatchingRequests.DirectComplete(Convert.FromHexString("78563412")) == 0x12345678,
            "570独立金样回传504原u32标识，不按席位或角色UID截断");
        foreach (int length in new[] { 0, 1, 3, 5 })
            Reject<InvalidDataException>(() => PvpMatchingRequests.DirectComplete(new byte[length]),
                "加载确认不接受空正文、截断或额外尾部");
        That(PvpMatchingPackets.Players(2, 0x12345678,
            [new(0x1234, 0, 0x11223344, 0x56, 0x789ABCDE, 0), new(0x5678, 1, 0x90ABCDEF, 0x12, 0x34567890, 1)])
            .SequenceEqual(Convert.FromHexString("0278563412023412004433221156DEBC9A7800785601EFCDAB90129078563401")),
            "504独立哨兵金样保留mode/u32标识/数量及每人13B原值，未混入总力摘要");
        That(PvpMatchingPackets.BeginLoading().Length == 0 && PvpMatchingPackets.LoadingState(2).SequenceEqual(new byte[] { 2 }),
            "505无正文、506单u8，不能追加应答成功前缀");
        Reject<InvalidDataException>(() => PvpMatchingPackets.Players(8, 1, [new(1, 0, 0, 0, 0, 0)]),
            "总力504不能省略每人固定三个角色摘要");
        Reject<InvalidDataException>(() => PvpMatchingPackets.Players(2, 1,
            [new(1, 0, 0, 0, 0, 0), new(1, 1, 0, 0, 0, 0)]), "504不能重复覆盖同一UID");
        Reject<InvalidDataException>(() => PvpMatchingPackets.Players(2, 0, [new(1, 0, 0, 0, 0, 0)]),
            "匹配标识保留0，不能发送没有预约身份的名单");
        Reject<InvalidDataException>(() => PvpMatchingPackets.LoadingState(0), "未闭合的506状态不能进入投影");
        var compact = PvpMatchingRequests.Match(Convert.FromHexString("01000000010100"));
        That(compact.Kind == 1 && compact.GameType == 0 && compact.Parameters.SequenceEqual(new byte[] { 0, 1, 1, 0 }) &&
            compact.Pairs.IsEmpty && compact.TrailingValue is null, "7B匹配独立金样保留四个选项，不追加不存在的列表");
        var extended = PvpMatchingRequests.Match(Convert.FromHexString("010700000100FF0201FF0307020178563412"));
        That(extended.Kind == 1 && extended.GameType == 7 && extended.Parameters.SequenceEqual(new byte[] { 0, 1, 1 }) &&
            extended.Pairs.SequenceEqual(new PvpMatchingPair[] { new(0, 255), new(2, 1), new(255, 3), new(7, 2) }) &&
            extended.TrailingValue == 0x12345678, "18B匹配独立金样保留四对字段及末尾u32，不误收14B外版布局");
        foreach (int length in new[] { 0, 3, 6, 8, 13, 14, 17, 19 })
            Reject<InvalidDataException>(() => PvpMatchingRequests.Match(new byte[length]), "匹配拒绝缺失及多余字段");
        That(PvpMatchingRequests.Cancel([1, 7, 0]) == (1, 7), "PK取消匹配严格读取kind与u16玩法");
        Reject<InvalidDataException>(() => PvpMatchingRequests.Cancel([1, 7]), "取消匹配不能省略玩法高字节");
        Reject<InvalidDataException>(() => PvpMatchingRequests.Cancel([1, 7, 0, 0]), "PK取消解析不吞其他玩法扩展");

        byte[] name = [2, 0, 0, 0, 0xB6, 0xD3];
        var team = PvpMatchingRequests.Team([..name, 7, 1, 5]);
        That(team.Name == "队" && team.CharacterIndices.SequenceEqual(new byte[] { 7, 1, 5 }),
            "总力873金样保留列表索引及顺序，不能按角色槽位重排");
        That(PvpMatchingRequests.CheckName(name) == "队" &&
            PvpMatchingRequests.Rename([..name, 0x34, 0x12]) == ("队", 0x1234),
            "874只带队名，875另带u16指定Main槽索引，保留完整宽度");
        foreach (var invalid in new byte[][] { [], [1, 0, 0, 0, 0x81], [0xFF, 0xFF, 0xFF, 0xFF],
            [1, 0, 0, 0, 0], [1, 0, 0, 0, 10], [..name, 0] })
            Reject<InvalidDataException>(() => PvpMatchingRequests.CheckName(invalid), "队名拒绝损坏编码、长度、控制字符和额外尾部");
        Reject<InvalidDataException>(() => PvpMatchingRequests.Team([..name, 0, 1]), "建队不能缺少第三个索引");
        Reject<InvalidDataException>(() => PvpMatchingRequests.Team([..name, 0, 1, 2, 3]), "建队不能附加第四个索引");
        Reject<InvalidDataException>(() => PvpMatchingRequests.Rename(name), "改名不能遗漏附加u16");
        byte[] chinese = [62, 0, 0, 0, ..Enumerable.Range(0, 31).SelectMany(_ => new byte[] { 0xB6, 0xD3 })];
        That(PvpMatchingRequests.CheckName(chinese).Length == 31, "名称容量按UTF16字符校验，兼容31个双字节字符");
        Reject<InvalidDataException>(() => PvpMatchingRequests.CheckName([32, 0, 0, 0, ..Enumerable.Repeat((byte)0x61, 32)]),
            "名称不能占满客户端32字符缓冲区的结尾NUL");
        That(PvpMatchingRequests.SelectCharacter([2]) == 2 && PvpMatchingRequests.SelectCharacter([3]) == 3,
            "选人仅解析原u8，已用尽等业务状态交用例拒绝，不借解析异常断开连接");
        Reject<InvalidDataException>(() => PvpMatchingRequests.SelectCharacter([]), "选人请求不能为空");
        Reject<InvalidDataException>(() => PvpMatchingRequests.SelectCharacter([0, 1]), "选人请求不能带额外字段");

        That(PvpMatchingPackets.Accepted(1, 7, 1).SequenceEqual(Convert.FromHexString("0101070001")) &&
            PvpMatchingPackets.Cancelled().SequenceEqual(new byte[] { 1 }) &&
            PvpMatchingPackets.Rejected(0x1234).SequenceEqual(new byte[] { 0, 0x34, 0x12 }),
            "匹配ACK金样区分520成功附加四字节、521无附加和公共u16错误");
        That(PvpMatchingPackets.Closed(1, 2).SequenceEqual(new byte[] { 1, 2 }),
            "NOTI460只含两个u8，不混入同号命令应答成功前缀");
        That(PvpMatchingPackets.TeamAccepted([7, 1, 5]).SequenceEqual(new byte[] { 1, 7, 1, 5 }) &&
            PvpMatchingPackets.Team("队", [7, 1, 5]).SequenceEqual(Convert.FromHexString("02000000B6D3070105")) &&
            PvpMatchingPackets.NameChanged("队").SequenceEqual(Convert.FromHexString("0102000000B6D3")),
            "873应答、804队伍通知和875改名应答使用各自金样，不重排三个原索引");
        That(PvpMatchingPackets.RoundSelection([new(0x12345678, 2), new(0x87654321, 0)]).SequenceEqual(
            Convert.FromHexString("78563412022143658700")), "806固定两份u32身份及u8选人索引，共十字节");
        Reject<InvalidDataException>(() => PvpMatchingPackets.TeamAccepted([1, 2]), "队伍回执不能少一个索引");
        Reject<InvalidDataException>(() => PvpMatchingPackets.Team("x", [0, 1, 2, 3]), "队伍通知不能多一个索引");
        Reject<InvalidDataException>(() => PvpMatchingPackets.NameChanged(new string('x', 32)), "改名通知预留结尾NUL");
        Reject<InvalidDataException>(() => PvpMatchingPackets.RoundSelection([new(1, 0), new(1, 1)]),
            "回合选人通知不能覆盖同一身份两次");
        Reject<InvalidDataException>(() => PvpMatchingPackets.RoundSelection([new(1, 3), new(2, 0)]),
            "业务拒绝的索引3不能进入开战投影");
        Console.WriteLine($"PASS {Count - before} 匹配与总力请求本版字节合同断言");
    }
}
