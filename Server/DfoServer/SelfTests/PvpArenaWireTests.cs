using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Game.Pvp;
using System.Collections.Immutable;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpArenaWireTests
{
    public static void RunCore()
    {
        int before = Count;
        That(PvpArenaRequests.Create([1, 0x34, 0x12, 0, 0, 0]) is
            { TitlePreset: 1, Title: "", MapIndex: 0x1234, Setting: 0, Kind: 0, Mode: 0 },
            "普通建房预设标题省略字符串，保持真实可变尾部");
        byte[] custom = [0, 2, 0, 0, 0, 0xD1, 0xB5, 0, 0, 1, 2, 0, 0, 0, 0x61, 0x62, 0, 0];
        That(PvpArenaRequests.Create(custom) is { Title: "训", Setting: 1, Password: "ab", Kind: 0, Mode: 0 },
            "普通自定义标题与密码各自按字节长度解析");
        That(PvpArenaRequests.Create([16, 0, 0, 1, 1, 0, 0, 0, 0x61, 0, 0]) is
            { TitlePreset: 16, Password: "a" }, "密码字符串不依赖自定义标题是否存在");
        foreach (var invalid in new byte[][] { [], [17, 0, 0, 0, 0, 0], custom[..^1], [..custom, 0],
            [0, 255, 255, 255, 255], [1, 0, 0, 2, 0, 0], [1, 0, 0, 1, 0, 0, 0, 0, 0, 0],
            [0, 1, 0, 0, 0, 0x81, 0, 0, 0, 0, 0], [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0] })
            Reject<InvalidDataException>(() => PvpArenaRequests.Create(invalid),
                "建房拒绝截断、额外尾部、非法标志、空密码及损坏字符串");
        That(PvpArenaRequests.Enter([0x34, 0x12, 0]) is { Room: 0x1234, Password: null } &&
            !PvpArenaRequests.Ready([0]) && PvpArenaRequests.Ready([1]) &&
            PvpArenaRequests.Mode([3]) == 3 && PvpArenaRequests.Map([0x78, 0x56]) == 0x5678,
            "普通入房、准备、模式及地图按独立writer宽度解析");
        Reject<InvalidDataException>(() => PvpArenaRequests.Enter([0x34, 0x12]), "入房不可省略选项字节");
        Reject<InvalidDataException>(() => PvpArenaRequests.Enter([0, 0, 0]), "零房间编号拒绝");
        That(PvpArenaRequests.Enter([0x34, 0x12, 1, 2, 0, 0, 0, 0x61, 0x62]) is
            { Room: 0x1234, Password: "ab" }, "密码对话框按标志1发送带长度密码串");
        foreach (var invalid in new byte[][] { [1, 0, 2], [1, 0, 1], [1, 0, 1, 0, 0, 0, 0],
            [1, 0, 1, 2, 0, 0, 0, 0x61], [1, 0, 0, 1], [1, 0, 1, 1, 0, 0, 0, 0x81] })
            Reject<InvalidDataException>(() => PvpArenaRequests.Enter(invalid), "入房拒绝错误标志、缺密码、损坏长度或编码及额外正文");
        Reject<InvalidDataException>(() => PvpArenaRequests.Ready([0, 1]), "准备不带伪造席位字节");
        Reject<InvalidDataException>(() => PvpArenaRequests.Ready([2]), "准备不是任意非零整数");
        Reject<InvalidDataException>(() => PvpArenaRequests.Mode([1, 0]), "模式不带额外队伍字节");
        Reject<InvalidDataException>(() => PvpArenaRequests.Map([1]), "地图要求完整u16");
        That(PvpArenaRequests.Connections([8, 7, 0, 6, 1, 5, 1, 4, 1, 3, 0, 2, 1, 1, 1, 0, 1]) is
            [true, true, true, false, true, true, true, false], "逐席连接标志按席位解释，0不能冒充已完成");
        foreach (var invalid in new byte[][] { [], [0], [8, 0, 1],
            [8, 0, 1, 0, 1, 2, 1, 3, 1, 4, 1, 5, 1, 6, 1, 7, 1],
            [8, 0, 2, 1, 1, 2, 1, 3, 1, 4, 1, 5, 1, 6, 1, 7, 1] })
            Reject<InvalidDataException>(() => PvpArenaRequests.Connections(invalid), "连接列表拒绝缺席、重复席及非法标志");
        That(PvpArenaRequests.Death([0x34, 0x12, 0x78, 0x56, 1, 0, 0, 0, 0, 0, 0]) == (0x1234, 0x5678),
            "死亡请求区分本人UID与击杀者UID");
        Reject<InvalidDataException>(() => PvpArenaRequests.Death(new byte[10]), "死亡报告截断拒绝");
        Reject<InvalidDataException>(() => PvpArenaRequests.Empty([0]), "加载完成拒绝多余正文");
        var timeout = PvpArenaRequests.Timeout(Convert.FromHexString(
            "000000000100000034120000FFFFFFFF00000080000000000000000078563412"));
        That(timeout.SequenceEqual(new uint[] { 0, 1, 0x1234, uint.MaxValue, 0x80000000, 0, 0, 0x12345678 }),
            "超时严格保留八席u32原值，业务校验不丢失符号位或空席字段");
        Reject<InvalidDataException>(() => PvpArenaRequests.Timeout(new byte[31]), "超时不能截短八席列表");
        Reject<InvalidDataException>(() => PvpArenaRequests.Timeout(new byte[33]), "超时不能附带额外字段");
        PvpArenaRequests.ResultEnd([]);
        Reject<InvalidDataException>(() => PvpArenaRequests.ResultEnd([0]), "结果返回不能附带伪造局ID或席位");
        var room = new PvpArenaRoom(0x1234, 0x128, "", PvpArenaMode.Team, 1, 2, "secret");
        var host = room.Join("host", 0x5678, 0)!;
        var guest = room.Join("guest", 0x4321, 1)!;
        That(room.SetReady(guest.Token, true), "包体夹具包含实际准备成员");
        That(PvpArenaPackets.Room(room.View()).SequenceEqual(Convert.FromHexString(
            "01003412020100010002017856022143FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0000000000")),
            "NOTI41预设标题金样独立核对房主、模式、双人席位和空席");
        That(PvpArenaPackets.Entered(room.View()).SequenceEqual(
            new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            "ACK51逐八席携带准备标志和连接标志");
        That(PvpArenaPackets.State(room.View()).SequenceEqual(Convert.FromHexString("3412010001000200000000")) &&
            PvpArenaPackets.Ready(1, true).SequenceEqual(new byte[] { 1, 1 }), "NOTI42和44独立固定宽度金样");
        That(PvpArenaPackets.SeatList(room.View()).SequenceEqual(Convert.FromHexString(
            "34120208000178560102214302FFFFFF03FFFFFF04FFFFFF05FFFFFF06FFFFFF07FFFFFF")),
            "NOTI43明确清空全部旧席位，不能只发目的席");
        That(room.Start(host.Token, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3)) is not null &&
            PvpArenaPackets.Start(room.View()).SequenceEqual(new byte[] { 1, 2 }) &&
            PvpArenaPackets.State(room.View()).SequenceEqual(Convert.FromHexString("3412020001000200000000")) &&
            PvpArenaPackets.Loaded(0x5678).SequenceEqual(new byte[] { 0x78, 0x56 }) &&
            PvpArenaPackets.Death(1, 0).SequenceEqual(new byte[] { 1, 0 }), "开战两字节、加载UID及死亡席位独立金样");
        room.Leave(host.Token);
        That(PvpArenaPackets.State(room.View()).AsSpan(3, 1).SequenceEqual(new byte[] { 1 }) &&
            !room.Member(guest.Token)!.Ready, "离房后房主变为原客人且准备取消");
        That(PvpArenaPackets.Rejected(true).SequenceEqual(new byte[] { 0, 4, 0 }) &&
            PvpArenaPackets.Rejected().SequenceEqual(new byte[] { 0, 19, 0 }), "入房拒绝区分密码对话框和通用拒绝");
        That(PvpArenaPackets.Left(room.View(), 3).SequenceEqual(Convert.FromHexString("341202010300FFFF")),
            "离房独立金样只清原本人席，不用完整八席空目录替代本人离房");
        ResultPacket();
        Console.WriteLine($"PASS {Count - before} 正式决斗请求及既有训练协议兼容断言");
        PvpMatchingWireTests.RunCore();
    }

    static void ResultPacket()
    {
        ImmutableArray<PvpArenaResultRow> rows =
        [
            new(0x1234, 0x01020304, 9, 0x05060708, 0x0A0B0C0D, 0x0E0F1011, 0x12131415),
            new(0x5678, 0x21222324, 0x29, 0x25262728, 0x2A2B2C2D, 0x2E2F3031, 0x32333435)
        ];
        var tail = new PvpArenaResultTail(0x4142, ushort.MaxValue, 0x43444546, 0x47, byte.MaxValue,
            0x48494A4B, [new(0x51525354, 0x5556, 0x57), new(0x61626364, 0x6566, 0x67)]);
        var body = PvpArenaResultPackets.Ordinary(7, 0x11223344, 0x55, 0x66778899, rows, tail);
        That(body.SequenceEqual(Convert.FromHexString(
            "0744332211559988776602" +
            "34120403020109080706050D0C0B0A11100F0E15141312" +
            "78562423222129282726252D2C2B2A31302F2E35343332" +
            "4241FFFF4645444347FF4B4A4948025453525156555764636261666567")),
            "NOTI47普通结果独立金样保留每行23字节、未知原值及两种缺席哨兵，追加项每个7字节");
        var withoutExtra = PvpArenaResultPackets.Ordinary(7, 0x11223344, 0x55, 0x66778899,
            rows, tail with { Extra = [] });
        That(withoutExtra.Length == 72 && withoutExtra.AsSpan(0, 71).SequenceEqual(body.AsSpan(0, 71)) &&
            withoutExtra[^1] == 0, "结果追加列表空时只写零计数，不改前面的提示、事件或角色值");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0,
            [rows[0], rows[0]], tail), "结果拒绝重复角色UID");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0,
            [rows[0] with { Character = ushort.MaxValue }], tail), "空席标识不能冒充结果角色UID");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0,
            Enumerable.Range(1, 9).Select(id => rows[0] with { Character = (ushort)id }).ToImmutableArray(), tail),
            "普通结果角色行不能超过八席");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0, default, tail),
            "结果必须显式提供角色列表");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0,
            rows, tail with { Extra = default }), "结果必须显式决定是否包含追加条目");
        Reject<InvalidDataException>(() => PvpArenaResultPackets.Ordinary(0, 0, 0, 0,
            rows, tail with { Extra = Enumerable.Repeat(tail.Extra[0], 256).ToImmutableArray() }),
            "结果追加数量不能截断成一个字节");
    }
}
