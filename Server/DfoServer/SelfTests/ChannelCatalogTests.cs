using System.Buffers.Binary;
using DfoServer.GameWorld.Channels;
using DfoServer.Network.Builders;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ChannelCatalogTests
{
    public static void Run()
    {
        int before = Count;
        const string row = "10 `<chn_channel_info_028>` 1 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0 ``";
        string source = "[server]\n2\n" + row + "\n[/server]\n";
        var beast = ChannelCatalog.Parse("[server]\n2\n" + row.Replace(" 1 `[granfloris]`", " 40 `[beast]`") + "\n[/server]\n");
        That(beast.Entry(10) is { Type: 40, Group: "[beast]" }, "外置频道保留改版魔兽独立身份");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "channel.txt");
            var embedded = Infrastructure.ServerRuntime.LoadChannels(path);
            // 决斗场入口沿用原位置，编号由 40 改为客户端 etc/pvpchannellist.etc 实际登记的 101。
            That(embedded.ServerIndex == 2 && embedded.Channels.Count >= 39 &&
                embedded.Channels.Take(38).Select(row => row.Number).SequenceEqual(
                    new[] { 1 }.Concat(Enumerable.Range(10, 37)).Select(number => number == 40 ? 101 : number)) &&
                embedded.Entry(10).Identity == new ChannelIdentity(2, 10),
                "无外置资源时从发行包内置脚本恢复全部入口");
            That(embedded.Channels.Single(row => row.Number == 39) ==
                new ChannelDefinition(39, "<chn_channel_info_048>", 45, "[fiendwar]"),
                "频道39进入团队频道超时空漩涡卡，类型45（CHANNEL_FIEND_WAR），名称使用客户端已有键");
            That(embedded.Channels.Single(row => row.Number == 101) ==
                new ChannelDefinition(101, "<chn_channel_info_039>", 24, "[pvp]"),
                "频道101进入决斗场，类型24，名称使用客户端已有键（客户端 pvpchannellist.etc 未登记40）");
            That(embedded.Channels.Single(row => row.Number == 41) ==
                new ChannelDefinition(41, "<chn_channel_info_062>", 54, "[evildom_war]"),
                "频道41为魔界大战：CHANNEL_EVILDOM_WAR 类型54、分组[evildom_war]、名称键062，排在普雷之前不改动已有入口端口");
            That(embedded.Channels.Single(row => row.Number == 42) ==
                new ChannelDefinition(42, "<chn_channel_info_054>", 50, "[prey]"),
                "频道42为普雷·伊希斯攻坚战：CHANNEL_PREY_RAID 类型50、分组[prey]、名称键054（伊希斯攻坚战），追加在末尾不改动已有入口端口");
            // 交易-拍卖行：PVF etc/channel_info.etc 客户端频道表写 `交易 - 拍卖行` 3（CHANNEL_TRADE）；ARAD 列表外层白名单 028FB090 含 3 不含 30
            // （CHANNEL_TRADE_GENERAL），特殊区域页分组表 01EBEC20 收录 [trade]。原 type 30 [goldenroad] 不进任何频道页。
            That(embedded.Channels.Single(row => row.Number == 32) ==
                new ChannelDefinition(32, "<chn_channel_info_002>", 3, "[trade]"),
                "频道32交易-拍卖行：CHANNEL_TRADE 类型3、分组[trade]、名称键002，进特殊区域页");
            That(embedded.Channels.Single(row => row.Number == 43) ==
                new ChannelDefinition(43, "<chn_channel_info_055>", 56, "[xian_village]"),
                "频道43五陵：CHANNEL_XIAN_VILLAGE 类型56、分组[xian_village]、名称键055，追加在末尾不改动已有入口端口");
            That(embedded.Channels.Single(row => row.Number == 44) == new ChannelDefinition(44, "<chn_channel_info_060>", 63, "[dnfaos_free]") &&
                embedded.Channels.Single(row => row.Number == 45) == new ChannelDefinition(45, "<chn_channel_info_061>", 62, "[dnfaos_rank]"),
                "频道44/45勇士对决-自由/排位：CHANNEL_DNFAOS_FREE 63、CHANNEL_DNFAOS_RANK 62，追加在末尾");
            That(embedded.Channels.Single(row => row.Number == 46) == new ChannelDefinition(46, "<chn_channel_info_057>", 53, "[evildom_gathering]") &&
                ChannelPackets.LoginChannelType(embedded.Entry(46)) == 53,
                "再战魔界追加真实type53和本版名称057，ACK LOGIN不降成普通0，既有端口顺序保持");
            That(embedded.Channels.Single(row => row.Number == 47) == new ChannelDefinition(47, "<chn_channel_info_045>", 8, "[pvp]") &&
                ChannelPackets.LoginChannelType(embedded.Entry(47)) == 8 && ChannelPackets.LoginChannelType(embedded.Entry(101)) == 24 &&
                ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: ChannelPackets.LoginChannelType(embedded.Entry(47)))[3] == 8,
                "无双频道末尾追加独立type8，保留传统24且LOGIN均发送真实类型，不复用旧频道端口");
            That(embedded.Channels.Single(row => row.Number == 51) == new ChannelDefinition(51, "<channel_info_cname_501>", 4, "[pvp]") &&
                ChannelPackets.LoginChannelType(embedded.Entry(51)) == 4 &&
                ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: ChannelPackets.LoginChannelType(embedded.Entry(51)))[3] == 4,
                "普通决斗场使用原生51/type4与已有新手名称键，追加末尾且LOGIN保留实际类型");
            That(embedded.Channels.Single(row => row.Number == 55) == new ChannelDefinition(55, "<channel_info_cname_505>", 13, "[pvp]") &&
                ChannelPackets.LoginChannelType(embedded.Entry(55)) == 13 &&
                ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: ChannelPackets.LoginChannelType(embedded.Entry(55)))[3] == 13,
                "自由练习场追加本服55/type13（新版集成自由练习场），使用自由练习名称且LOGIN保留真实类型");
            That(embedded.Channels[^3].Number == 50 && embedded.Channels[^3].Type == 55 &&
                embedded.Channels[^3].NameReference == "<chn_channel_info_059>" &&
                ChannelPackets.LoginChannelType(embedded.Entry(50)) == 55,
                "强者之路追加独立50号/type55原生入口，使用本版名称059并保留既有频道端口顺序");
            That(embedded.Channels[^2].Number == 56 && embedded.Channels[^2].Type == 64 &&
                embedded.Channels[^2].NameReference == "<chn_channel_info_063>" &&
                embedded.Channels[^2].Group == "[evildom_war_challenge]" && ChannelPackets.LoginChannelType(embedded.Entry(56)) == 64,
                "英雄频道独立56号/type64只追加在原末尾，不改变旧频道端口顺序");
            foreach (int number in new[] { 101, 47, 51, 55 })
            {
                string channelRow = embedded.SourceText.Split('\n').Single(line => line.StartsWith(number + " ", StringComparison.Ordinal));
                string[] fields = channelRow.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                That(fields[9] == "50" && fields[10] == "95",
                    "实际决斗入口等级列与Town10/0的50级门槛及95级上限一致：" + number);
                int minimumGrade = int.Parse(fields[5], System.Globalization.CultureInfo.InvariantCulture);
                int maximumGrade = int.Parse(fields[6], System.Globalization.CultureInfo.InvariantCulture);
                foreach (int grade in new[] { 0, 1, 5, 20, 29 })
                    That(minimumGrade <= grade && grade <= maximumGrade,
                        $"实际PK入口{number}按原生01705040准入决斗等级{grade}，不将有段位角色锁在频道列表外");
            }
            That(embedded.Channels.Single(row => row.Number == 48) == new ChannelDefinition(48, "<chn_channel_info_049>", 39, "[tag_tournament]") &&
                ChannelPackets.LoginChannelType(embedded.Entry(48)) == 39 && !ChannelAreaLanding.SameSnapshotPredicates(22, 39),
                "帝国竞技场追加type39并隔离换频道快照，避让PK频道47");
            That(embedded.Channels.Single(row => row.Number == 49) is { Type: 40, Group: "[beast]" },
                "内置目录发布独立49/type40魔兽频道");
            That(embedded.Channels.Single(row => row.Number == 24).Type == 11 && embedded.Channels.Single(row => row.Number == 24).Group == "[ancient]",
                "频道24仍是远古分组，类型11才会出现在远古地下城卡片");
            That(embedded.Channels.Single(row => row.Number == 35).Type == 11 && embedded.Channels.Single(row => row.Number == 35).Group == "[crack]",
                "频道35仍是裂缝分组，类型11才会出现在异次元裂缝卡片");
            That(embedded.Channels.Single(row => row.Number == 23).Type == 11 && embedded.Channels.Single(row => row.Number == 26).Type == 11
                && embedded.Channels.Single(row => row.Number == 20).Type == 32,
                "亡者峡谷、异界地下城和卢克的类型保持已确认的值");
            File.WriteAllText(path, source);
            That(Infrastructure.ServerRuntime.LoadChannels(path).SourceText == source, "目录部署优先使用外置频道脚本");
            File.WriteAllText(path, "invalid");
            Reject<InvalidDataException>(() => Infrastructure.ServerRuntime.LoadChannels(path), "外置脚本错误仍拒绝，不静默覆盖配置");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        var catalog = ChannelCatalog.Parse(source);
        foreach (var (type, expected) in new (int Type, byte Expected)[] { (22, 22), (54, 54), (55, 55), (56, 0), (57, 0) })
        {
            byte loginType = ChannelPackets.LoginChannelType(new(new(2, 10), type, "[granfloris]"));
            byte[] endpoint = ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: loginType);
            That(loginType == expected, $"LOGIN类型投影保留CHANNEL_DPL_PVE 55及既有类型边界：type{type}");
            That(endpoint[3] == expected, $"ACK LOGIN正文模式字段发送真实频道类型且不扩大白名单：type{type}");
        }
        foreach (int pvpType in new[] { 4, 5, 8, 13, 24 })
            That(!ChannelAreaLanding.SameSnapshotPredicates(22, pvpType) &&
                !ChannelAreaLanding.SameSnapshotPredicates(pvpType, 22) &&
                ChannelAreaLanding.SameSnapshotPredicates(24, pvpType) &&
                ChannelAreaLanding.LandsInChannelTown(new(new(2, 101), pvpType, "[pvp]")),
                "决斗入口独立于普通快照且直接入城，决斗入口之间可保留决斗地区");
        That(ChannelAreaLanding.SameSnapshotPredicates(53, 53) && !ChannelAreaLanding.SameSnapshotPredicates(22, 53) &&
            !ChannelAreaLanding.SameSnapshotPredicates(53, 22), "再战vt+38独立参与换频道快照，同类型保留、普通互换不保留");
        That(ChannelAreaLanding.SameSnapshotPredicates(55, 55), "强者之路同类型频道保留城镇快照");
        foreach (int type in new[] { 22, 54, 39, 53 })
            That(!ChannelAreaLanding.SameSnapshotPredicates(55, type) && !ChannelAreaLanding.SameSnapshotPredicates(type, 55),
                $"原生vt+48独立比较强者之路55，进出type{type}不保留城镇快照");
        That(!ChannelAreaLanding.SameSnapshotPredicates(22, 56) && !ChannelAreaLanding.SameSnapshotPredicates(56, 22) &&
            ChannelAreaLanding.SameSnapshotPredicates(56, 56), "五陵补丁独立比较类型，普通互换不保留快照，同类型仍保留");
        foreach (int type in new[] { 11, 22, 56, 24, 39, 53, 54, 32 })
            That(!ChannelAreaLanding.SameSnapshotPredicates(11, type) && !ChannelAreaLanding.SameSnapshotPredicates(type, 11),
                "type11各分组进出均重新入房，同类型也不能继承另一特殊地区快照");
        That(!ChannelAreaLanding.ClientKeepsTownSnapshot(null, new(new(2, 24), 11, "[ancient]"), new(38, 0, 622, 196)),
            "无频道历史时type11仍不恢复普通城镇快照");
        That(ChannelAreaLanding.ClientKeepsTownSnapshot(null, new(new(2, 46), 53, "[evildom_gathering]"), new(62, 0, 543, 255)) &&
            !ChannelAreaLanding.ClientKeepsTownSnapshot(null, new(new(2, 46), 53, "[evildom_gathering]"), new(38, 0, 543, 255)),
            "没有进程内频道历史时Stadium存档仍有53候选，不把普通城镇当53快照");
        That(catalog.ServerIndex == 2 && catalog.Channels.Count == 1 && catalog.Channels[0] ==
            new ChannelDefinition(10, "<chn_channel_info_028>", 1, "[granfloris]"), "频道分组、频道号、类型与标签分开保留");
        That(catalog.Entry(10) == new ChannelEntry(new(2, 10), 1, "[granfloris]"), "会话入口保留频道身份及资源分类，不另建业务实例");
        Reject<InvalidDataException>(() => catalog.Entry(256), "无法放入Actor身份的频道号不能建立监听入口");
        That(ChannelCatalog.Parse(source.Replace("<chn_channel_info_028>", "<4::channel_info_dname_1>")).Channels[0].Number == 10,
            "允许已有的带索引字符串引用");
        foreach (int type in new[] { 4, 5, 8, 13, 24 })
        {
            string GradeSource(string minimum, string maximum) => source.Replace("[/server]",
                $"101 `<chn_channel_info_039>` {type} `[pvp]` 0 {minimum} {maximum} 0 0 50 95 0 0 0 0 ``\n[/server]");
            foreach (var (minimum, maximum) in new[] { ("0", "0"), ("0", "29"), ("5", "9"), ("20", "20"), ("0.0", "29.0"), ("0", "255") })
                That(ChannelCatalog.Parse(GradeSource(minimum, maximum)).Entry(101).Type == type,
                    "合法决斗等级范围允许按需限段，零段专用配置仍有效");
            foreach (var (minimum, maximum) in new[] { ("-1", "29"), ("5", "4"), ("0", "256"), ("0.5", "29"), ("0", "28.5") })
                Reject<InvalidDataException>(() => ChannelCatalog.Parse(GradeSource(minimum, maximum)),
                    "决斗等级列拒绝负数、倒置、超出Actor字节或非整数");
            string PvpSource(string minimum, string maximum) => source.Replace("[/server]",
                $"101 `<chn_channel_info_039>` {type} `[pvp]` 0 0 0 0 0 {minimum} {maximum} 0 0 0 0 ``\n[/server]");
            foreach (var (minimum, maximum) in new[] { ("1", "95"), ("50", "95"), ("95", "95"), ("50.0", "95.0") })
                That(ChannelCatalog.Parse(PvpSource(minimum, maximum)).Entry(101).Type == type,
                    "决斗频道接受有效整数等级范围及其浮点写法");
            foreach (var (minimum, maximum) in new[] { ("0", "0"), ("0", "95"), ("50", "0"), ("96", "95"), ("50", "96"), ("50.5", "95"), ("50", "94.5") })
                Reject<InvalidDataException>(() => ChannelCatalog.Parse(PvpSource(minimum, maximum)),
                    "决斗频道拒绝零值、倒置、越界或非整数等级范围");
        }
        foreach (string bad in new[]
        {
            source.Replace("`<chn_channel_info_028>`", "`plain name`"),
            source.Replace("`<chn_channel_info_028>`", "`<bad/key>`"),
            source.Replace(" 1 `[granfloris]`", " 201 `[granfloris]`"),
            source.Replace(" 1 `[granfloris]`", " 57 `[granfloris]`"),
            source.Replace(" 5 0", " 256 0"),
            source.Replace(" 5 0", " 5 NaN"),
            source.Replace(" 5 0", " 5"),
            source.Replace("``", "0 ``"),
            source.Replace("``", "`"),
            source.Replace("10 `<", "11 `<"),
            source.Replace("\n2\n", "\n250\n"),
            source.Replace("[/server]", row + "\n[/server]"),
            source + source,
            source + "\0"
        })
            Reject<InvalidDataException>(() => ChannelCatalog.Parse(bad), "畸形或无法在本版使用的频道声明被拒绝");
        byte[] body = LoginPackets.ChannelInfo(catalog.GameIdentity);
        for (int i = 0; i < body.Length; i++)
            body[i] = (byte)((((body[i] << 6) | (body[i] >> 2)) ^ 0xb5) & 0xff);
        That(BinaryPrimitives.ReadInt32LittleEndian(body) == body.Length - 4, "完整CHANNELINFO长度前缀包含时钟字段");
        Bytes(body.AsSpan(343, 4), [0x38, 2, 0x40, 10], "登录安装共享的服务器2/游戏频道10，不使用点击的分类号");
        using (var reader = new BinaryReader(new MemoryStream(body, 347, body.Length - 347)))
        {
            That(reader.ReadByte() == 88 && Math.Abs(reader.Read7BitEncodedInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 5,
                "CHANNELINFO field11为服务器Unix秒，绝对期限不能落到零时钟");
            Bytes(reader.ReadBytes(10), [0xa0, 0xd3, 0x0e, 0xd4, 0x0d, 0xa8, 0xd3, 0x0e, 0x94, 0x0e], "频道身份不截断协议数量");
            That(reader.BaseStream.Position == reader.BaseStream.Length, "握手时钟及枚举数量完整消费，无多余尾部");
        }
        var character = new Character(1, 0, "A", 0, new(38, 1, 450, 234), null);
        var stats = new CharacterStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        byte[] actor = TownPackets.Actor(character, 0, catalog.GameIdentity);
        byte[] player = TownPackets.Player(character, stats, 1, catalog.GameIdentity);
        Bytes(actor.AsSpan(0, 5), [0, 1, 0, 2, 10], "Actor按NOTI1身份进入本地角色表，不能沿用0/0");
        Bytes(player.AsSpan(0, 5), [1, 1, 0, 2, 10], "Player延续Actor的服务器和频道上下文");
        Bytes(actor.AsSpan(actor.Length - 23), [6, 0, 0, 0, 0, 100, 0, 0, 2, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0],
            "Actor独立23B尾向量包含城镇血蓝百分比、真实原属大区并保留其余字段");
        Bytes(player.AsSpan(5), TownPackets.Player(character, stats, 1).AsSpan(5), "身份修正不改变Player属性与装备布局");
        That(actor[3] == body[344] && actor[4] == body[346] && player[3] == body[344] && player[4] == body[346],
            "复现原生本地/跨频道比较，登录和角色包选择同一个角色存储上下文");
        foreach (byte server in new byte[] { 0, 1, 2, 44 })
        {
            foreach (var scene in new[] { ActorSceneState.Town, ActorSceneState.Dungeon })
            {
                var role = character with { Name = "大区" + server, VisibilityFlags = 6 };
                byte[] projected = TownPackets.Actor(role, 0, new(server, 11), sceneState: scene);
                int nameWidth = CharacterName.Encoding.GetByteCount(role.Name);
                That(projected.Length == 243 + nameWidth && projected[132 + nameWidth] == (byte)scene,
                    "变长姓名与城镇/副本状态保留原Actor总长度及独立状态偏移");
                Bytes(projected.AsSpan(0, 5), [0, 1, 0, server, 11], "头部大区与频道号各自保留");
                Bytes(projected.AsSpan(projected.Length - 23),
                    [6, 0, 0, 0, 0, scene == ActorSceneState.Town ? (byte)100 : (byte)0, 0, 0, server, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0],
                    "独立尾向量确认场景百分比及显示位后8B的实际大区，不硬改0配置、不覆盖FF哨兵");
            }
        }
        Console.WriteLine($"PASS {Count - before} 频道资源字段、原生名称引用与共享身份断言");
    }
}
