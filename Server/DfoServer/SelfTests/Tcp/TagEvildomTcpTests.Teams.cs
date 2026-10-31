using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static void RequestTeamRoster(TcpPeer peer)
    {
        peer.Send(1470); peer.Expect(0, 1408, [0]); peer.Barrier();
    }

    static async Task VerifyMultipleTeamsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T08:00:00+08:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "tag-evildom-teams"), resources: resources, clock: clock);
        var roles = new List<Character>();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            foreach (string name in new[] { "TagOne", "TagTwo", "TagThree", "TagFour", "TagFive", "TagSix" })
            {
                var plan = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room,
                    resources.Towns.InitialReturn, resources.Learning.Get);
                roles.Add(repository.Create(owner, plan with { Level = 95,
                    InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                        plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning) }));
            }
        }
        await fixture.StartAsync();
        int port = ChannelTcpTests.Endpoints(fixture)[46];
        byte[] two = TeamRows(0, 1, 3, 2, -1, -1), three = TeamRows(0, 1, 3, 2, 5, 4);
        using (var peer = Open(fixture, gamePort: port, channelNumber: 46))
        {
            peer.Select(room: false, tagEvildom: [1677, 1678, 1393, 1394]);
            string firstOnly = Configuration();
            // 已有第一队时登录三、四、三不会凭登录顺序暗改第二队；提示必须导向真实设置窗口。
            foreach (ushort index in new ushort[] { 2, 3, 2 })
            {
                ReturnToSelection(peer);
                peer.Select(index, room: false, tagEvildom: [1677, 1678, 1709]);
                byte[] body = peer.TagEvildomInfo[^1].Body;
                int titleLength = checked((int)U32(body, 0));
                int textLength = checked((int)U32(body, 4 + titleLength));
                string notice = System.Text.Encoding.UTF8.GetString(body, 8 + titleLength, textLength);
                That(notice.Contains("TagOne", StringComparison.Ordinal) && notice.Contains("队伍设置", StringComparison.Ordinal) &&
                    !notice.Contains("资料需要更新", StringComparison.Ordinal), "非队首提示指明真实队首与设置入口，不误报技能需要更新");
                RequestTeamRoster(peer);
                That(Configuration() == firstOnly, "1470及分别登录第三、第四名角色不隐式新增或重排编队");
            }
            string assets = Economy(fixture);
            Save(two, 1);
            That(fixture.Query("SELECT first0,second0,first1,second1,selected_team FROM account_tag_evildom_configuration") is
                [[long a, long b, long c, long d, 1L]] && a == roles[0].Id && b == roles[1].Id &&
                c == roles[3].Id && d == roles[2].Id && Economy(fixture) == assets,
                "已有第一队时新增第二队保存客户端反序稳定ID，不按角色ID排序、不改资产");
            string saved = Configuration();
            Save(two, 1);
            That(Configuration() == saved, "重复保存第二队不推进配置revision");
            ReturnToSelection(peer);
            peer.Select(3, room: false, tagEvildom: [1677, 1678, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[2].Body, [2, 3, 2, 2], "重选第二队队首后安装第二队原始名册索引");
            RequestTeamRoster(peer);
            Save(three, 2);
            ReturnToSelection(peer);
            peer.Select(5, room: false, tagEvildom: [1677, 1678, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[0].Body, three, "第三队保存与重选完整恢复全部三队");
            Bytes(peer.TagEvildomInfo[2].Body, [2, 5, 2, 4], "第三队按客户端提交反序安装双角色");
            RequestTeamRoster(peer);
            saved = Configuration(); assets = Economy(fixture);
            peer.Send(1662, TeamSetting(TeamRows(0, 1, 3, 2, 5, 0), 2));
            peer.Expect(1, 1662, [0, 19, 0]); peer.Expect(0, 1709); peer.Barrier();
            That(Configuration() == saved && Economy(fixture) == assets, "跨队重复成员拒绝且保留三队与资产");

            // popup301只发送7→4重选队首，不会再次发送1662；按真实客户端流程切换已有三队。
            string assignments = Assignments();
            int selectedTeam = 2;
            foreach (var selection in new[] { (Index: (ushort)0, Team: 0), (Index: (ushort)3, Team: 1),
                (Index: (ushort)5, Team: 2), (Index: (ushort)5, Team: 2) })
            {
                string before = Configuration();
                ReturnToSelection(peer);
                peer.Select(selection.Index, room: false, tagEvildom: [1677, 1678, 1393, 1394]);
                Bytes(peer.TagEvildomInfo[0].Body, three, "已有队伍切换后仍完整恢复三队原始成员");
                byte standby = selection.Team switch { 0 => 1, 1 => 2, _ => 4 };
                Bytes(peer.TagEvildomInfo[2].Body, [2, (byte)selection.Index, 2, standby],
                    "仅重选队首也安装其所属队伍，不能留下空运行头像");
                That(fixture.Query("SELECT selected_team FROM account_tag_evildom_configuration") is [[long team]] &&
                    team == selection.Team && Assignments() == assignments && Economy(fixture) == assets,
                    "切队保存实际选中队伍，不改三队成员或角色资产");
                if (selection.Team == selectedTeam) That(Configuration() == before, "重复重选同一队首不推进配置revision");
                selectedTeam = selection.Team;
                saved = Configuration();
            }
            ReturnToSelection(peer);
            peer.Select(4, room: false, tagEvildom: [1677, 1678, 1709]);
            That(Configuration() == saved, "只登录备用成员不倒置或重新分配已有队伍");
            ReturnToSelection(peer);
            peer.Select(5, room: false, tagEvildom: [1677, 1678, 1393, 1394]);
            VerifyDirectEntry(peer);
            EnterTag(peer, 9608, uint.MaxValue);
            ReturnTag(peer);

            void Save(byte[] teams, int selected)
            {
                peer.Send(1662, TeamSetting(teams, selected));
                peer.Expect(1, 1662, TeamAccepted(teams, selected));
                peer.Expect(0, 1677, teams); peer.Expect(0, 1678); peer.Barrier();
            }
        }
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture, gamePort: ChannelTcpTests.Endpoints(fixture)[46], channelNumber: 46))
        {
            peer.Select(5, room: false, tagEvildom: [1677, 1678, 1393, 1394]);
            Bytes(peer.TagEvildomInfo[0].Body, three, "服务重开和重新选角保留三队及第三队选择");
            Bytes(peer.TagEvildomInfo[2].Body, [2, 5, 2, 4], "重开服务后仍使用真实第三队");
            RequestTeamRoster(peer);
        }

        string Configuration() => fixture.Snapshot("SELECT * FROM account_tag_evildom_configuration");
        string Assignments() => fixture.Snapshot("SELECT first0,second0,first1,second1,first2,second2 FROM account_tag_evildom_configuration");
    }

    static byte[] TeamRows(params int[] indices) => Build(w =>
    { foreach (int index in indices) { w.Write(index < 0 ? (byte)0 : (byte)2); w.Write(index); w.Write(0u); } });
    static byte[] TeamSetting(byte[] teams, int selected) => Build(w =>
    { w.Write(new byte[13]); w.Write(teams); w.Write(selected); w.Write(0); });
    static byte[] TeamAccepted(byte[] teams, int selected) => Build(w =>
    { w.Write((byte)1); w.Write(TeamSetting(teams, selected)); });
}
