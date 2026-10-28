using DfoServer.Game.Accounts;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildGovernanceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-governance"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, memberId;
        using (var masterOwner = owners.Acquire("10000", () => { }))
        using (var memberOwner = owners.Acquire("20001", () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            masterId = repository.Create(masterOwner, new("GovMaster", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            memberId = repository.Create(memberOwner, new("GovMember", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(81,'治理公会','governance-guild',$p0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(81,$p0,0,1600000000),(81,$p1,4,1600000000)
            """, masterId, memberId);
        await fixture.StartAsync();
        using (var master = Open(fixture))
        {
            byte region = Enter(master);
            using (var member = Open(fixture, "20001"))
            {
                That(Enter(member) == region, "治理测试双方处于同一权威大区");
                string assetsBefore = fixture.Snapshot("SELECT id,account,revision,gold,name FROM characters ORDER BY id");
                member.Send(832, []);
                member.Expect(1, 832, [0, 19, 0]);
                master.Send(832, [10]);
                master.Expect(1, 832, [0, 7, 0]);
                master.Send(832, []);
                master.Expect(1, 832, [1]);
                Refresh(master, 1, 4, 0, "普通会员", recommended: (region, 10));
                Refresh(member, 4, 4, 0, "普通会员", recommended: (region, 10));
                string recommendation = fixture.Snapshot("SELECT * FROM guild_recommended_channels");
                string revision = fixture.Snapshot("SELECT revision FROM guild_settings WHERE guild_id=81");
                master.Send(832, []);
                master.Expect(1, 832, [1]);
                Refresh(master, 1, 4, 0, "普通会员", recommended: (region, 10));
                Refresh(member, 4, 4, 0, "普通会员", recommended: (region, 10));
                That(fixture.Snapshot("SELECT * FROM guild_recommended_channels") == recommendation &&
                    fixture.Snapshot("SELECT revision FROM guild_settings WHERE guild_id=81") == revision,
                    "推荐当前频道以空请求提交，重复提交保留频道与关系版本");
                uint promotionPermission = GuildGovernanceRules.Promotion;
                master.Send(748, Build(writer => { writer.Write((byte)4); writer.Write(promotionPermission); }));
                master.Expect(1, 748, [1]);
                Refresh(master, 1, 4, promotionPermission, "普通会员");
                Refresh(member, 4, 4, promotionPermission, "普通会员");
                That(fixture.Query("SELECT permissions FROM guild_roles WHERE guild_id=81 AND grade=4") is [[32L]],
                    "真实748权限修改落库并回放到双方NOTI70");

                var promotion = new GuildSettingsChange(GuildGovernanceRules.Promotion, "获授权的介绍", true, uint.MaxValue, 255);
                member.Send(740, GuildGovernanceTests.Settings(promotion));
                member.Expect(1, 740, [1]);
                Refresh(master, 1, 4, promotionPermission, "普通会员");
                Refresh(member, 4, 4, promotionPermission, "普通会员");
                That(fixture.Query("SELECT promotion||':'||webpage||':'||tags||':'||join_mode FROM guild_settings WHERE guild_id=81")
                    is [["获授权的介绍:1:0:0"]],
                    "真实740仅修改选中的介绍及网页字段，不采用过期未选值");

                member.Send(748, [4, 0, 0, 0, 0]);
                member.Expect(1, 748, [0, 36, 0]);
                master.Send(749, [4, .. GuildGovernanceTests.Text("见习成员")]);
                master.Expect(1, 749, [1]);
                Refresh(master, 1, 4, promotionPermission, "见习成员");
                Refresh(member, 4, 4, promotionPermission, "见习成员");
                master.Send(126, [region, .. GuildGovernanceTests.Text("GovMember"), 3]);
                master.Expect(1, 126, [1, 0, region, .. GuildGovernanceTests.Text("GovMember"), 3]);
                Refresh(master, 1, 4, promotionPermission, "见习成员");
                Refresh(member, 3, 4, promotionPermission, "见习成员", gradeChanged: true);
                member.Send(126, [region, .. GuildGovernanceTests.Text("GovMaster"), 4]);
                member.Expect(1, 126, [0, 83, 0]);
                That(fixture.Query("SELECT grade FROM guild_members WHERE guild_id=81 AND character_id=$p0", memberId) is [[3L]],
                    "126完整回调、成员行和目标自身127品级保持一致");

                member.Send(740, GuildGovernanceTests.Settings(promotion));
                member.Expect(1, 740, [0, 7, 0]);
                master.Send(749, [4, .. GuildGovernanceTests.Text(new string('a', 21))]);
                master.Expect(1, 749, [0, 159, 0]);
                master.Send(748, [4, 0, 0]);
                master.Expect(1, 748, [0, 48, 0]);
                master.Send(156, GuildGovernanceTests.Text("GovFresh"));
                master.Expect(1, 156, [1]);
                That(fixture.Snapshot("SELECT id,account,revision,gold,name FROM characters ORDER BY id") == assetsBefore,
                    "治理功能只改变公会设置和成员职位，不触碰双方完整角色资产与版本");
            }
            // 重连重新读取持久设置，不能靠旧在线会话内的临时名字或权限蒙混。
            using var reconnected = Open(fixture, "20001");
            Enter(reconnected);
            reconnected.Send(339, []);
            CheckRole(reconnected.Expect(0, 70).Body, 4, GuildGovernanceRules.Promotion, "见习成员");
            reconnected.ReadGuildNpcs();
            reconnected.ReadGuildDungeon();
            That(fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
                "职位编辑与重新登录后的公会关系外键完整");
        }
        await fixture.StopAsync();
        Console.WriteLine($"PASS {Count - before} 公会治理授权、回调、在线刷新及重连 TCP 断言");
    }

    static byte Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        byte? region = null;
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 0 && packet.Opcode == 2 && packet.Body is [1, ..] && packet.Body.Length > 3)
                region = packet.Body[3];
            if (packet.Kind == 0 && packet.Opcode == 124)
            {
                peer.ReadWeeklyDungeon();
                return region ?? throw new InvalidOperationException("治理TCP未读取本人Actor大区");
            }
        }
        throw new InvalidOperationException("治理TCP未完成选角入城");
    }

    static void Refresh(TcpPeer peer, byte selfGrade, byte role, uint permissions, string name, bool gradeChanged = false,
        (byte Region, byte Channel)? recommended = null)
    {
        if (gradeChanged) peer.Expect(0, 127, [selfGrade]);
        CheckRole(peer.Expect(0, 70).Body, role, permissions, name, recommended);
        var members = GuildPanelTests.DecodeMembers(peer.Expect(1, 140).Body, true, true);
        That(members.GuildId == 81 && members.Total == 2 && members.Rows.Length == 2,
            "治理提交刷新公会信息与成员列表，仅本人职位实际改变时发送127，不重置普通编辑中的界面");
        peer.Expect(1, 1454);
        peer.Expect(1, 744);
        peer.ReadGuildDungeon();
    }

    static void CheckRole(byte[] body, byte grade, uint permissions, string name,
        (byte Region, byte Channel)? recommended = null)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        string Text() => DfoServer.Game.Characters.CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
        Text();
        reader.ReadBytes(5);
        Text();
        reader.ReadBytes(11);
        Text();
        Text();
        reader.ReadUInt16();
        Text();
        reader.ReadBytes(28 * grade);
        uint mask = reader.ReadUInt32();
        byte[] raw = reader.ReadBytes(24);
        int end = Array.IndexOf(raw, (byte)0);
        That(raw.Length == 24 && end >= 0 && mask == permissions &&
            DfoServer.Game.Characters.CharacterName.Encoding.GetString(raw.AsSpan(0, end)) == name &&
            raw.AsSpan(end).ToArray().All(value => value == 0),
            "NOTI70实际固定职位记录包含已提交掩码与中文标签并保留NUL补齐");
        reader.ReadBytes(28 * (5 - grade));
        byte benefits = reader.ReadByte();
        reader.ReadBytes(benefits * 10 + 10);
        uint region = reader.ReadUInt32(), channel = reader.ReadUInt32();
        if (recommended is { } target)
            That(region == target.Region && channel == target.Channel,
                "NOTI70推荐大区及频道均占四字节并来自当前已提交推荐");
    }
}
