using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PlayerInteractionTests
{
    public static void RunCore()
    {
        int before = Count;
        That(ChannelPackets.Endpoint("127.0.0.1", 12345, channelType: 22)[3] == 22 &&
            ChannelPackets.Endpoint("127.0.0.1", 12345, channelType: 59)[3] == 59,
            "LOGIN模式跟随实际频道类型，普通与特殊入口不能固定为0或22");
        var create = PartyRequests.Information(Convert.FromHexString("000404000000000500000000000007"));
        That(create.Operation == 0 && create.Settings is { TitleCode: 4, Capacity: 4, Dungeon: 0, Difficulty: 5, Option: 7 },
            "真实寻找队伍创建15字节请求保留所有字段");
        byte[] search = Convert.FromHexString("030001000000000000000000001C0003000000000100000100070000000000000000000000000000000000000000000000000000000000000000");
        That(PartyRequests.Directory(search) is { Actor: 3, Page: 0, Sort: 1, Recruiting: true, Kind: 0, Options: 7, Keyword: "" },
            "真实J面板1404从18B路由头后读取40B查询，不把频道当作页码");
        Reject<InvalidDataException>(() => PartyRequests.Directory(search[..^1]), "队伍查询截断拒绝");
        Reject<InvalidDataException>(() => PartyRequests.Directory([..search, 0]), "队伍查询额外尾部拒绝");
        byte[] badSearch = search.ToArray(); Array.Fill(badSearch, (byte)65, 26, 32);
        Reject<InvalidDataException>(() => PartyRequests.Directory(badSearch), "队伍查询定长字符串必须终止");
        Bytes(PartyPackets.Directory(2, 28, [], 0), [0, 0, 0, 0],
            "空查询发送页数0的NOTI9，隐藏旧行且不进入空页纠正的重复查询分支");
        That(BitConverter.ToUInt16(PartyPackets.Directory(2, 28, [], 31), 2) == 4,
            "查询第二个u16是十行一页的总页数，不是9999增量哨兵或队伍总数");
        byte[] titled = Convert.FromHexString("010004000000B6D3CEe902785634120903020104060508");
        var edit = PartyRequests.Information(titled);
        That(edit.Operation == 1 && edit.Settings is { Title: "队伍", Capacity: 2, Dungeon: 0x12345678,
            Difficulty: 9, Argument: 0x203, Flag: 1, Kind: 4, KindArgument: 0x506, Option: 8 },
            "自定义标题按CP936长度前缀，副本号为u32而非S4的u16");
        Reject<InvalidDataException>(() => PartyRequests.Information(titled[..^1]), "队伍设置截断拒绝");
        Reject<InvalidDataException>(() => PartyRequests.Information([..titled, 0]), "队伍设置额外尾部拒绝");
        byte[] LongTitle(int count) => DfoServer.Testing.TcpPeer.Build(w =>
        {
            w.Write((byte)0); w.Write((byte)0); w.Write(count); w.Write(Enumerable.Repeat((byte)65, count).ToArray());
            w.Write(Convert.FromHexString("04000000000500000000000007"));
        });
        That(PartyRequests.Information(LongTitle(63)).Settings.Title.Length == 63, "64B标题缓冲保留末尾NUL");
        Reject<InvalidDataException>(() => PartyRequests.Information(LongTitle(64)), "标题不能越过原生64B缓冲");
        That(PartyRequests.Information(Convert.FromHexString("0104040000000005000000010000")).Settings.Option is null,
            "原生operation1/kind1快捷修改的14字节包不误判为截断");
        byte[] coded = PartyPackets.Information(1, 2, 10, [(0, 42)], 0, false, create.Settings);
        That(coded[12] == 4 && coded[13] == 0 && coded[14] == 4 && BitConverter.ToUInt32(coded, 15) == 0 &&
            coded[19] == 5 && coded[36] == 7 && coded[37] == 1,
            "预设标题不写字符串，人数上限与完整info块不挤占名册数量");
        byte[] endpointBody = Convert.FromHexString("051A19ACF81A19ACF80075000200000C000000303065303163356130343163");
        var endpoint = PartyRequests.Endpoint(endpointBody);
        Bytes(PartyPackets.Realtime([(0x1234, 2)]), Convert.FromHexString("013412640002"),
            "城镇队伍HUD按本版Data+35C安装100百分比及真实槽位");
        That(PartyRequests.Leader([1]) == 1, "真实委任121请求读取目标槽位而非角色ID");
        Reject<InvalidDataException>(() => PartyRequests.Leader([]), "委任请求不能缺少槽位");
        Reject<InvalidDataException>(() => PartyRequests.Leader([1, 0]), "委任请求不能携带多余字段");
        Bytes(PartyPackets.Members(0x1234, 2, 10, [(2, 0x2345), (0, 0x3456)], 2),
            Convert.FromHexString("01000F273412020A000200020056340000000000024523000000000000000200000000000000"),
            "action2跳过设置段，按稳定槽序发送成员和实际队长，再以空扩展结束");
        That(endpoint is { Nat: 5, InnerAddress: 0xF8AC191A, OuterAddress: 0xF8AC191A, Port: 29952, Mtu: 512 },
            "真实UDP报告独立解析地址、端口与MTU");
        Reject<InvalidDataException>(() => PartyRequests.Endpoint(endpointBody[..^1]), "UDP机器标识长度不符拒绝");
        Reject<InvalidDataException>(() => PartyRequests.Endpoint([..endpointBody, 0]), "UDP额外尾部拒绝");
        byte[] invalidPort = endpointBody.ToArray(); invalidPort[9] = invalidPort[10] = 0;
        Reject<InvalidDataException>(() => PartyRequests.Endpoint(invalidPort), "UDP零端口拒绝");
        byte[] invalidMtu = endpointBody.ToArray(); invalidMtu[11] = invalidMtu[12] = 0;
        Reject<InvalidDataException>(() => PartyRequests.Endpoint(invalidMtu), "UDP非法MTU拒绝");
        byte[] menu = Convert.FromHexString("34120F00000000000000000000");
        That(CharacterRequests.TryPeerMenu(menu, out ushort target) && target == 0x1234, "实测13B右键请求识别目标");
        That(!CharacterRequests.TryPeerMenu([0x34, 0x12, 2, 0, 0, 0, 0, 0, 0, 0, 0], out _),
            "其他peer模式保持未实现，不套用mode15布局");
        byte[] auxiliary = menu.ToArray();
        auxiliary[9] = 1;
        That(!CharacterRequests.TryPeerMenu(auxiliary, out _), "未知辅助字段不返回通用成功");
        Reject<InvalidDataException>(() => CharacterRequests.TryPeerMenu(menu[..^1], out _), "截断的mode15拒绝");
        Bytes(TownPackets.PeerMenu(0x1234), Convert.FromHexString("34120F00000000FFFF"),
            "NOTI7 mode15独立客户端消费布局：目标、模式、公共u32、无队伍FFFF");
        That(CharacterRequests.TryOtherPlayer([0x34, 0x12, 3], out target) && target == 0x1234 &&
            !CharacterRequests.TryOtherPlayer([255, 255, 2], out _) && CharacterRequests.IsRoster([255, 255, 2]),
            "公开资料不吞掉既有选角名册握手");
        Reject<InvalidDataException>(() => CharacterRequests.TryOtherPlayer([1, 0, 3, 0], out _), "资料请求尾部拒绝");
        var character = new Character(0x1234, 0, "PublicTarget", 1, new(38, 0, 622, 196), null)
        {
            EquipmentSlotFlags = 56,
            Skills = [new(5, 2), new(46, 3)],
            Learning = CharacterLearningState.Uninitialized with { Experience = 0x0102030405060708UL },
            Guild = new(9, "目标公会", 1, 1, "会长", "", 0, 0)
        };
        var stats = new DfoServer.GameWorld.Characters.CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        byte[] body = TownPackets.OtherPlayer(character, stats, 37, new(64, 12), DateTimeOffset.UnixEpoch);
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        That(reader.ReadByte() == 3 && reader.ReadUInt16() == 1 && reader.ReadByte() == 64 && reader.ReadByte() == 12,
            "USERINFO公开资料分派及目标频道");
        That(reader.ReadUInt32() == 37 && reader.ReadUInt16() == 0 && reader.ReadByte() == 1 &&
            reader.ReadByte() == 1 && reader.ReadByte() == 1 && reader.ReadUInt16() == 0x1234 &&
            reader.ReadUInt64() == 0x0102030405060708UL, "9B公开头后紧跟目标ID和完整u64经验，不使用17B本人头");
        That(reader.ReadUInt32() == 89 && reader.ReadUInt32() == 8550 && reader.ReadUInt32() == 5150,
            "目标属性块长度和生命魔法值保持");
        reader.ReadBytes(81);
        That(reader.ReadByte() == 56 && reader.ReadByte() == 0 && reader.ReadUInt32() == 0 && reader.ReadByte() == 0,
            "装备标志、空装备列表及装备尾部");
        That(reader.ReadUInt64() == 0 && reader.ReadByte() == 0, "装备后直接读8B名称装饰和技能树，不插入本人BUFF登记块");
        for (int tree = 0; tree < 2; tree++)
            That(reader.ReadByte() == 2 && reader.ReadUInt16() == 5 && reader.ReadByte() == 2 &&
                reader.ReadUInt16() == 46 && reader.ReadByte() == 3, "公开技能树完整消费：" + tree);
        That(reader.ReadByte() == 0 && reader.ReadBytes(21).All(value => value == 0), "公开状态和决斗场空列表不偏移");
        That(reader.ReadUInt16() == 0 && reader.ReadUInt32() == 0, "公会辅助字段保留");
        int width = reader.ReadInt32();
        That(CharacterName.Encoding.GetString(reader.ReadBytes(width)) == "目标公会" && reader.ReadUInt16() == 0,
            "公开公会名使用CP936长度前缀");
        That(reader.ReadUInt32() == 0 && reader.ReadByte() == 0 && reader.ReadByte() == 0 && stream.Position == stream.Length,
            "公开尾部列表与状态完全消费，没有本人的皮肤仓库数据");
        Console.WriteLine($"PASS {Count - before} 玩家菜单与公开资料协议断言");
    }
}
