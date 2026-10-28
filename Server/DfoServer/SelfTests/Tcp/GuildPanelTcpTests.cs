using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildPanelTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-panel"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, peerId;
        using (var master = owners.Acquire("10000", () => { }))
        using (var peer = owners.Acquire("20001", () => { }))
        using (var outsider = owners.Acquire("20002", () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            masterId = repository.Create(master, new("PanelMaster", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            peerId = repository.Create(peer, new("PanelPeer", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            repository.Create(outsider, new("PanelAlone", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter });
        }
        // 批量成员只存在于本组隔离库；不加载它们的背包或启动额外客户端。
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,notice,created_at_unix)
            VALUES(71,'成员公会','panel-guild',$p0,'公会公告',1600000000),
                (72,'其他公会','other-guild',2000,'其他公告',1600000000);
            WITH RECURSIVE n(value) AS (VALUES(1) UNION ALL SELECT value+1 FROM n WHERE value<130)
            INSERT INTO characters(id,account,slot,name,town,area,x,y,direction,level,job,grow_type)
            SELECT 1000+value,'offline-'||value,0,'Offline'||value,0,0,100,100,0,50,2,49 FROM n;
            UPDATE characters SET account='10000',slot=1 WHERE id=1130;
            INSERT INTO characters(id,account,slot,name,town,area,x,y,direction)
            VALUES(2000,'foreign-member',0,'ForeignOnly',0,0,100,100,0);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(71,$p0,0,1600000000),(71,$p1,0,1600000000),(72,2000,0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,member_memo,joined_at_unix,last_login_unix)
            SELECT 71,id,4,'中文成员备注',1600000000,1700000000 FROM characters WHERE id BETWEEN 1001 AND 1130;
            INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,last_clear_unix) VALUES(71,8500,34,1700000000);
            """, masterId, peerId);
        await fixture.StartAsync();
        using (var master = Open(fixture))
        using (var peer = Open(fixture, "20001"))
        using (var outsider = Open(fixture, "20002"))
        {
            Enter(master, 1);
            Enter(peer, 4);
            Enter(outsider, 100);
            string membersBefore = fixture.Snapshot("SELECT * FROM guild_members ORDER BY guild_id,character_id");
            string assetsBefore = fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id");
            master.Send(1302, []);
            Bytes(master.Expect(1, 1302).Body, [1, 132, 0, .. new byte[12]],
                "红包列表以真实132名成员及空交易记录完整回复，页签无需等待缺失响应");
            master.Send(1304, new byte[17]);
            master.Expect(1, 1304, new byte[7]);
            master.Send(1304, []);
            master.Expect(1, 1304, new byte[7]);
            outsider.Send(1302, []);
            outsider.Expect(1, 1302, new byte[7]);
            master.Send(1305, new byte[18]);
            master.Expect(1, 1305, new byte[3]);
            master.Send(1306, new byte[17]);
            master.Expect(1, 1306, new byte[3]);

            // 先刷新信息，随后完整列表必须独立完成，不能等待不存在的同号339 ACK。
            master.Send(339, []);
            CheckInformation(master.Expect(0, 70).Body, 132, 2);
            master.ReadGuildNpcs();
            master.ReadGuildDungeon();
            master.Send(140, Build(writer => writer.Write(72u)));
            CheckInformation(master.Expect(0, 70).Body, 132, 2);
            var first = GuildPanelTests.DecodeMembers(master.Expect(1, 140).Body, true, true);
            var next = GuildPanelTests.DecodeMembers(master.Expect(1, 141).Body, false, true);
            var rows = first.Rows.Concat(next.Rows).ToArray();
            master.ReadGuildNpcs();
            That(first.GuildId == 71 && first.Name == "成员公会" && first.Total == 132 && first.Rows.Length == 128 &&
                next.GuildId == 71 && next.Rows.Length == 4 && rows.Select(row => row.Id).Distinct().Count() == 132,
                "真实TCP一次请求推齐128+4成员，续包不会覆盖总数或漏重成员");
            That(rows.All(row => row.Id != 2000) && rows.Single(row => row.Id == masterId).Grade == 1 &&
                rows.Single(row => row.Id == peerId) is { Grade: 4, Channel: 10 } &&
                rows.Count(row => row.Channel == 255) == 130,
                "异会引用不越权，存量grade0展示正确且只有真实在线成员使用在线频道");
            That(rows.Single(row => row.Id == masterId) is { Representative: 1 } &&
                rows.Single(row => row.Id == 1130) is { Representative: 0, Channel: 255 } alternate &&
                alternate.Reference == masterId &&
                rows.GroupBy(row => row.Reference).All(group => group.Count(row => row.Representative == 1) == 1),
                "真实TCP完整名册每个账号组均有唯一代表，首包在线主角色与末包离线副角色正确关联");
            That(rows.Single(row => row.Id == masterId) is { WeeklyContribution: 100, Contribution: 100 } &&
                rows.Single(row => row.Id == peerId) is { WeeklyContribution: 100, Contribution: 100 } &&
                rows.Where(row => row.Channel == 255).All(row => row.WeeklyContribution == 0 && row.Contribution == 0),
                "真实入城登录贡献100同时进入本周与累计字段，离线夹具不虚增贡献");

            master.Send(67, []);
            CheckInformation(master.Expect(0, 70).Body, 132, 2);
            var ordinary = GuildPanelTests.DecodeMembers(master.Expect(1, 67).Body, true, false);
            var ordinaryNext = GuildPanelTests.DecodeMembers(master.Expect(1, 139).Body, false, false);
            master.ReadGuildNpcs();
            That(ordinary.Total == 132 && ordinary.Rows.Length + ordinaryNext.Rows.Length == 132,
                "空正文67路径同样通过真实注册表回完整首续包");

            master.Send(1455, []);
            var firstGrades = GuildPanelTests.DecodeGrades(master.Expect(1, 1455).Body, true);
            var nextGrades = GuildPanelTests.DecodeGrades(master.Expect(1, 1456).Body, false);
            var grades = firstGrades.Rows.Concat(nextGrades.Rows).ToArray();
            That(firstGrades.GuildId == 71 && firstGrades.Total == 132 && firstGrades.Rows.Length == 128 &&
                nextGrades.GuildId == 71 && nextGrades.Total is null && nextGrades.Rows.Length == 4 &&
                grades.Select(row => row.Name).Distinct().Count() == 132 && grades.All(row => row.Region == 2) &&
                grades.Single(row => row.Name == "PanelMaster").Grade == 1 &&
                grades.Single(row => row.Name == "PanelPeer").Grade == 4 &&
                grades.All(row => row.Name != "ForeignOnly"),
                "真实1455请求经正式注册表推齐128+4职位，真实会长及普通成员映射且不泄露异会成员");

            // 用后继查重响应作处理完成哨兵；不靠等待超时声称畸形请求保持静默。
            master.Send(140, [1, 2, 3]);
            master.Send(67, [0]);
            master.Send(339, [0]);
            master.Send(1455, [0]);
            master.Send(156, Text("PanelFresh"));
            master.Expect(1, 156, [1]);
            outsider.Send(140, Build(writer => writer.Write(71u)));
            outsider.Expect(1, 140, [0, 0, 0]);
            outsider.Send(1455, []);
            outsider.Expect(1, 1455, [0, 0, 0]);
            outsider.Send(156, Text("PanelOther"));
            outsider.Expect(1, 156, [1]);
            That(fixture.Snapshot("SELECT * FROM guild_members ORDER BY guild_id,character_id") == membersBefore &&
                fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") == assetsBefore,
                "刷新、成员查询、畸形请求和无公会拒绝均不改写成员及角色资产");
        }
        using (var rankingPeer = TcpPeer.Open(fixture))
        {
            Enter(rankingPeer, 1);
            rankingPeer.Send(1506, [255, 255]);
            byte[] summary = rankingPeer.Expect(1, 1506).Body;
            That(summary.Length == 116 && BitConverter.ToInt32(summary, 0) == 0 && BitConverter.ToInt32(summary, 4) > 0 &&
                BitConverter.ToSingle(summary, 8) > 0 && BitConverter.ToInt32(summary, 12) == 0,
                "1506真实Runtime返回当前公会116B统计，首DWORD零初始化，外来UID引用不改为查询别会，也不伪造上月");
            rankingPeer.Send(1507, [0, 0, 1, 0]);
            byte[] list = rankingPeer.Expect(1, 1507).Body;
            That(list.Length == 157 && list[0] == 0 && U16(list, 1) == 2 && U16(list, 3) == 2 &&
                U32(list, 5) == 71 && U16(list, 79) == 132,
                "1507从真实两公会生成76B排行行，正文无前置标志位，成员数使用132而非页数");
            rankingPeer.Send(1507, [1, 2, 1, 0]);
            byte[] top = rankingPeer.Expect(1, 1507).Body;
            That(top is [1, ..] && U16(top, 3) == 2, "前三名模式按原生模式字节回包");
            rankingPeer.Send(1507, [0, 0, 2, 0]);
            rankingPeer.Expect(1, 1507, [0, 2, 0, 0, 0]);
            rankingPeer.Send(1506, []); rankingPeer.Expect(1, 1506, new byte[116]);
            rankingPeer.Send(1507, [0, 8, 1, 0]); rankingPeer.Expect(1, 1507, new byte[5]);
            rankingPeer.Send(156, Text("RankBarrier")); rankingPeer.Expect(1, 156, [1]);
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
            "公会成员查询结束后隔离库及外键完整");
        Console.WriteLine($"PASS {Count - before} 公会面板刷新、成员分块与隔离 TCP 断言");
    }

    static void Enter(TcpPeer peer, byte grade)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        bool ownGrade = false, information = false;
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 4)
                peer.Id = U16(packet.Body, 9);
            if (packet.Kind == 0 && packet.Opcode == 2 && packet.Body is [0, ..])
                That(packet.Body[^15] == packet.Body[3], "本人Actor独立尾部大区与头部一致，140能按大区和姓名关联本人");
            if (packet.Kind == 0 && packet.Opcode == 127)
            {
                Bytes(packet.Body, [grade], "入城独立初始化本人职位，旧grade0按实际会长身份投影");
                ownGrade = true;
            }
            if (packet.Kind == 0 && packet.Opcode == 70)
            {
                That(ownGrade, "公会信息及权限表到达前已安装本人职位，不保留Actor缺省职位6");
                information = true;
            }
            if (packet.Kind == 0 && packet.Opcode == 124)
            {
                That(ownGrade && information == (grade != 100), "有无公会的登录均安装准确职位且不伪造会籍");
                peer.ReadWeeklyDungeon();
                return;
            }
        }
        throw new InvalidOperationException("公会成员TCP未完成入城序列");
    }

    static void CheckInformation(byte[] body, ushort members, ushort online)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        string name = ReadText(reader);
        reader.ReadBytes(2);
        ushort count = reader.ReadUInt16();
        reader.ReadByte();
        string notice = ReadText(reader);
        reader.ReadBytes(3);
        reader.ReadUInt32();
        reader.ReadUInt32();
        string master = ReadText(reader), displayMaster = ReadText(reader);
        That(name == "成员公会" && count == members && notice == "公会公告" &&
            master == "PanelMaster" && displayMaster == master && reader.ReadUInt16() == online,
            "NOTI70使用数据库真实公会信息与当前在线人数，不发送虚构刷新成功包");
    }

    static string ReadText(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
    static byte[] Text(string text) => Build(writer =>
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    });
}
