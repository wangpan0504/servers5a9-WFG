using Microsoft.Data.Sqlite;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SocialTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        AccountFriendGroupTests.RunCore();
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "social"), resources: resources);
        await fixture.StartAsync();
        ushort alphaId;
        string[] alphaNames = ["一二三四五", "Team B", "!", "好友"];
        string[] betaNames = ["Raid", "Farm", "Trade", "伙伴"];
        byte[] alphaIcons = [7, 0, 4, 2], betaIcons = [1, 6, 3, 5];
        byte[] GroupRequest(byte[] icons, string[] names) => Build(w =>
        {
            w.Write(new byte[] { 0x91, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x72 });
            w.Write(icons);
            foreach (string name in names)
            {
                byte[] bytes = CharacterName.Encoding.GetBytes(name);
                w.Write(bytes); w.Write(new byte[11 - bytes.Length]);
            }
        });
        byte[] GroupInfo(byte[] icons, string[] names) => Build(w =>
        {
            for (int i = 0; i < 4; i++) { w.Write(icons[i]); WriteName(w, names[i]); }
        });
        byte[] alphaGroups = GroupInfo(alphaIcons, alphaNames), betaGroups = GroupInfo(betaIcons, betaNames);
        using (var alpha = Open(fixture, "sociala"))
        using (var beta = Open(fixture, "socialb"))
        {
            alpha.Create("SocialA"); beta.Create("SocialB");
            var repository = new CharacterRepository(fixture.Database);
            var owners = new AccountOwners();
            alpha.Select(); beta.Select();
            alphaId = alpha.Id;
            That(alpha.CharacterFriends.AsSpan().SequenceEqual(new byte[8]) && beta.AccountFriends.AsSpan().SequenceEqual(new byte[1]),
                "登录回放空角色/冒险团好友，不创建虚假关系");
            Bytes(alpha.AccountFriendGroups, Build(w => { for (byte group = 0; group < 4; group++) { w.Write(group); WriteName(w, ""); } }),
                "首次入城已初始化四个冒险团分组，不依赖先打开L面板或发送1684");
            alpha.Send(1684);
            alpha.Expect(0, 1702, Build(w => { for (byte group = 0; group < 4; group++) { w.Write(group); WriteName(w, ""); } }));
            Refresh(alpha, 0);
            string[] changedNames = ["临时分组", "Stage B", "StageC", "伙伴"];
            byte[] changedIcons = [6, 5, 1, 7];
            alpha.Send(1683, GroupRequest(changedIcons, changedNames)); alpha.Expect(1, 1683, [1]);
            alpha.Expect(0, 1702, GroupInfo(changedIcons, changedNames));
            That(fixture.Query("SELECT slot,icon,name FROM account_social_groups WHERE account='sociala' ORDER BY slot") is
                [[0L, 6L, "临时分组"], [1L, 5L, "Stage B"], [2L, 1L, "StageC"], [3L, 7L, "伙伴"]],
                "首次保存按ACK再1702即时回放已提交四组，不需1684或重选角色");
            alpha.Send(1683, GroupRequest(alphaIcons, alphaNames)); alpha.Expect(1, 1683, [1]);
            alpha.Expect(0, 1702, alphaGroups);
            That(fixture.Query("SELECT slot,icon,name FROM account_social_groups WHERE account='sociala' ORDER BY slot") is
                [[0L, 7L, "一二三四五"], [1L, 0L, "Team B"], [2L, 4L, "!"], [3L, 2L, "好友"]],
                "连续再次保存提交后按ACK再1702回放，10B名称保持完整");
            beta.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_social_groups WHERE account='socialb'") is [[0L]],
                "连续两次保存仅通知本人，不向另一账号发布或写入分组");
            alpha.Send(1684); alpha.Expect(0, 1702, alphaGroups); Refresh(alpha, 0);
            alpha.Send(120); alpha.Expect(1, 120, [1, 0]);
            alpha.Send(290, Build(w => { w.Write(beta.Id); w.Write((byte)2); WriteName(w, "SocialB"); }));
            alpha.Expect(1, 290, [1]);
            var friends = Refresh(alpha, 1);
            That(U32(friends, 4) == 1 && fixture.Query("SELECT owner_id,target_id,kind FROM character_social_links") is [[long, long, 0L]],
                "290提交后回真实全量好友且只建立单向边");
            beta.Barrier();
            Execute("UPDATE account_adventures SET name=CASE account WHEN 'sociala' THEN 'TeamA' ELSE 'TeamB' END,name_initialized=1");
            byte[] Named(byte mode, string value, string target = "SocialB", byte server = 2) => Build(w =>
            {
                w.Write(mode); w.Write((ushort)1); w.Write(0u); WriteName(w, value); WriteName(w, target); w.Write(server);
            });
            alpha.Send(17, Named(36, "一对一"));
            var incoming = beta.Expect(0, 65, Build(w => { w.Write((byte)36); w.Write((byte)0); WriteName(w, "SocialA"); w.Write((byte)2);
                WriteName(w, "一对一"); WriteName(w, "TeamA"); WriteName(w, "SocialA"); })).Body;
            // 按消费者的字段顺序取窗口对象再回复，不能在测试里硬编码正确目标掩盖错误下行。
            using var messageReader = new BinaryReader(new MemoryStream(incoming));
            string MessageName() => System.Text.Encoding.GetEncoding(936).GetString(messageReader.ReadBytes(checked((int)messageReader.ReadUInt32())));
            messageReader.ReadUInt16(); MessageName(); messageReader.ReadByte(); MessageName(); MessageName();
            string replyTarget = MessageName();
            That(replyTarget == "SocialA" && messageReader.BaseStream.Position == incoming.Length,
                "接收方1vs1窗口按发送者寻址，不能把自己设为回复对象");
            beta.Send(17, Named(40, "一对一回复", replyTarget));
            alpha.Expect(0, 65, Build(w => { w.Write((byte)36); w.Write((byte)0); WriteName(w, "SocialB"); w.Write((byte)2);
                WriteName(w, "一对一回复"); WriteName(w, "TeamB"); WriteName(w, "SocialB"); }));
            alpha.Send(17, Named(1, "悄悄话"));
            beta.Expect(0, 65, Build(w => { w.Write((byte)1); w.Write((byte)0); WriteName(w, "SocialA"); w.Write((byte)2);
                WriteName(w, "悄悄话"); WriteName(w, "TeamA"); }));
            beta.Send(17, Named(41, "悄悄话回复", "SocialA"));
            alpha.Expect(0, 65, Build(w => { w.Write((byte)41); w.Write((byte)0); WriteName(w, "SocialB"); w.Write((byte)2);
                WriteName(w, "悄悄话回复"); WriteName(w, "TeamB"); }));
            beta.Send(17, Named(41, "异服拒绝", "SocialA", 5)); beta.Barrier();
            alpha.NoFrame(150, "名字回复仍拒绝其他服务器，不能按同名角色误投", (0, 65));
            beta.Send(118, BlockBody("SocialA", "勿扰"));
            var added = beta.Expect(1, 118).Body;
            That(added[0] == 1 && U32(added, 1) == 7 && DecodeName(added, 5, 7) == "SocialA", "黑名单新增按当前客户端独立ACK布局回真实对象");
            Refresh(beta, 0);
            beta.Send(120);
            var blocked = beta.Expect(1, 120).Body;
            That(blocked[0] == 1 && blocked[1] == 1, "空120读取持久黑名单");
            beta.Send(1538, Build(w => { WriteName(w, "SocialA"); WriteName(w, "新备注"); }));
            That(beta.Expect(1, 120).Body is [1, 1, ..] && fixture.Query("SELECT memo FROM character_social_links WHERE kind=1") is [["新备注"]],
                "1538不造无消费者ACK，以真实120列表同步备注");
            alpha.Send(17, Named(36, "屏蔽测试")); alpha.Barrier();
            beta.NoFrame(150, "被拉黑者的1对1消息不投递", (0, 65));
            beta.Send(119, BlockBody("SocialA", ""));
            beta.Expect(1, 119, Build(w => { w.Write((byte)1); w.Write(1u); WriteName(w, "SocialA"); })); Refresh(beta, 0);
            alpha.Send(1439, Build(w => { WriteName(w, "TeamB"); WriteName(w, "这是申请留言不是角色名"); w.Write((byte)1); WriteName(w, "发起方备注"); }));
            alpha.Expect(1, 1439, Build(w => { w.Write((byte)1); WriteName(w, "TeamB"); w.Write((byte)1); WriteName(w, "发起方备注"); }));
            byte[] Request(bool outgoing, string name) => Build(w =>
            {
                w.Write((byte)1); w.Write(outgoing ? (byte)1 : (byte)0); WriteName(w, name); WriteName(w, "这是申请留言不是角色名"); w.Write(0x7ffeae7fu);
            });
            Refresh(alpha, 1, Request(true, "TeamB")); Refresh(beta, 0, Request(false, "TeamA"));
            beta.Send(1684);
            beta.Expect(0, 1702, Build(w => { for (byte group = 0; group < 4; group++) { w.Write(group); WriteName(w, ""); } }));
            Refresh(beta, 0, Request(false, "TeamA"));
            beta.Send(1683, GroupRequest(betaIcons, betaNames)); beta.Expect(1, 1683, [1]);
            beta.Expect(0, 1702, betaGroups);
            beta.Send(1684); beta.Expect(0, 1702, betaGroups); Refresh(beta, 0, Request(false, "TeamA"));
            alpha.Send(1684); alpha.Expect(0, 1702, alphaGroups); Refresh(alpha, 1, Request(true, "TeamB"));
            That(fixture.Query("SELECT count(*) FROM account_social_groups") is [[8L]], "两个账号各自保存四组，回放互不覆盖");
            That(fixture.Query("SELECT count(*) FROM account_social_links") is [[0L]], "申请仅保存pending");
            beta.Send(1440, Build(w => { WriteName(w, "TeamA"); w.Write((byte)4); WriteName(w, "队友"); }));
            beta.Expect(1, 1440, Build(w => { w.Write((byte)1); WriteName(w, "TeamA"); w.Write((byte)4); WriteName(w, "队友"); }));
            Refresh(beta, 0); Refresh(alpha, 1);
            That(fixture.Query("SELECT count(*) FROM account_social_links") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM account_social_requests") is [[0L]], "接受双向好友和消费申请同次提交");
            That(fixture.Query("SELECT owner_account,memo,group_id FROM account_social_links ORDER BY owner_account") is
                [["sociala", "发起方备注", 1L], ["socialb", "队友", 4L]], "双方使用各自选择的分组和备注，不误用本服server2");
            beta.Send(1443, Build(w => { WriteName(w, "TeamA"); WriteName(w, "固定队"); w.Write((byte)3); }));
            beta.Expect(1, 1443, Build(w => { w.Write((byte)1); WriteName(w, "TeamA"); WriteName(w, "固定队"); w.Write((byte)3); }));
            Refresh(beta, 0);
            fixture.Sql("UPDATE account_adventures SET capsule_experience=123,brave_experience=456,points=$p0 WHERE account='socialb'",
                Enumerable.Repeat((byte)1, 20).ToArray());
            alpha.Send(1403, Build(w => w.Write(beta.Id)));
            var adventure = alpha.Expect(1, 1403).Body;
            That(adventure[0] == 1 && U16(adventure, 1) == beta.Id && U32(adventure, 15) == 7440,
                "他人冒险团查询回目标专属资料");
            That(adventure.AsSpan(19 + 5272, 40).SequenceEqual(new byte[40]) && adventure.AsSpan(19 + 7424, 8).SequenceEqual(new byte[8]),
                "公开冒险团不泄漏真实商店余额、勇猛经验或胶囊余额");
            alpha.Barrier(); beta.Barrier();
            PartyInteraction(fixture, alpha, beta);
            using (var owner = owners.Acquire("socialb", () => { }))
                repository.Create(owner, CharacterCreation.Prepare("SocialBAlt", resources.Characters.Get(1),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            ushort original = beta.Id;
            foreach (ushort slot in new ushort[] { 1, 0 })
            {
                beta.Send(7);
                if (slot == 1)
                {
                    beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
                    alpha.Expect(0, 6, Build(w => w.Write(original)));
                }
                beta.Expect(1, 7, [1]); beta.Expect(0, 2);
                Refresh(alpha, 1, accountOnline: false, characterOnline: false, accountAdventure: "TeamB");
                beta.Select(slot);
                Bytes(beta.AccountFriendGroups, betaGroups, "换角登录1702恢复账号四组，不退回默认或其他账号配置");
                Refresh(alpha, 1, characterOnline: slot == 0, accountAdventure: "TeamB",
                    accountCharacter: slot == 0 ? "SocialB" : "SocialBAlt");
                That(slot == 0 ? beta.Id == original : beta.Id != original,
                    "换角使用真实不同Actor，冒险团好友仍只覆盖TeamB这一行");
                beta.Barrier();
            }
            That(fixture.Query("SELECT count(*) FROM account_social_links") is [[2L]],
                "换角不新增账号好友边，双方始终各一条冒险团关系");
        }
        VerifyPartyDirectory(fixture);
        using (var rejected = Open(fixture, "groupsreject"))
        {
            rejected.Create("GroupsReject"); rejected.Select();
            rejected.Send(1683, GroupRequest(betaIcons, betaNames)); rejected.Expect(1, 1683, [1]);
            rejected.Expect(0, 1702, betaGroups);
            Execute("CREATE TRIGGER fail_group_packet BEFORE INSERT ON account_social_groups WHEN NEW.account='groupsreject' AND NEW.slot=3 BEGIN SELECT RAISE(ABORT,'fixture'); END");
            rejected.Send(1683, GroupRequest(alphaIcons, alphaNames)); rejected.Closed();
            That(fixture.Query("SELECT name FROM account_social_groups WHERE account='groupsreject' ORDER BY slot") is
                [["Raid"], ["Farm"], ["Trade"], ["伙伴"]], "1683晚期SQL异常整笔回滚且不发成功ACK或1702");
            Execute("DROP TRIGGER fail_group_packet");
        }
        using (var rejected = Open(fixture, "groupsreject"))
        {
            rejected.Select(); Bytes(rejected.AccountFriendGroups, betaGroups, "存储异常后重连仍回放原持久四组");
            rejected.Send(1683, new byte[48]); rejected.Closed();
            That(fixture.Query("SELECT count(*) FROM account_social_groups WHERE account='groupsreject'") is [[4L]],
                "截断1683沿原非法包路径关闭，不误ACK或修改已存配置");
        }
        await fixture.StopAsync(); await fixture.StartAsync("social-reopen", isolatedProcess: true);
        using (var alpha = Open(fixture, "sociala"))
        {
            alpha.Select();
            Bytes(alpha.AccountFriendGroups, alphaGroups, "独立进程冷启动后1702恢复实际名称与图标");
            alpha.Send(1684); alpha.Expect(0, 1702, alphaGroups);
            Refresh(alpha, 1, accountOnline: false, characterOnline: false);
            That(alpha.Id == alphaId && U32(alpha.CharacterFriends, 4) == 1 && alpha.AccountFriends[0] == 1,
                "冷启动后登录恢复角色好友和账号好友");
            VerifyAccountPresence(alpha.AccountFriends, false, "TeamB", "SocialB");
            That(fixture.Query("SELECT memo,group_id FROM account_social_links WHERE owner_account='sociala'") is [["发起方备注", 1L]],
                "冷启动恢复申请者独立分组与备注");
            alpha.Send(291, Build(w => { w.Write((byte)2); WriteName(w, "SocialB"); }));
            alpha.Expect(1, 291, [1]); Refresh(alpha, 0, accountOnline: false);
            alpha.Send(1441, Build(w => WriteName(w, "TeamB")));
            alpha.Expect(1, 1441, Build(w => { w.Write((byte)1); WriteName(w, "TeamB"); }));
            alpha.Expect(0, 1378, Build(w => WriteName(w, "TeamB"))); Refresh(alpha, 0);
            That(fixture.Query("SELECT count(*) FROM account_social_links") is [[0L]], "冒险团好友删除双方边并同步删除通知");
        }
        // 喇叭目标沿既有1439精确账号名寻址：完整32B可用，短前缀和同账号都不能命中。
        using (var requester = Open(fixture, "targetrequest"))
        using (var target = Open(fixture, "targetlong"))
        {
            requester.Create("TargetAsk"); target.Create("TargetLong");
            requester.Select(); target.Select();
            string fullName = new('测', 16), prefixName = new('测', 8);
            fixture.Sql("UPDATE account_adventures SET name='TargetRequester',name_initialized=1 WHERE account='targetrequest'");
            fixture.Sql("UPDATE account_adventures SET name=$p0,name_initialized=1 WHERE account='targetlong'", fullName);
            byte[] Add(string name) => Build(w =>
            {
                WriteName(w, name); WriteName(w, "喇叭邀请"); w.Write((byte)0); WriteName(w, "");
            });
            requester.Send(1439, Add(prefixName)); requester.Expect(1, 1439, [0, 3, 0]);
            That(fixture.Query("SELECT count(*) FROM account_social_requests WHERE source_account='targetrequest'") is [[0L]],
                "16B前缀不误命中32B冒险团名，不产生错误邀请");
            requester.Send(1439, Add(fullName));
            requester.Expect(1, 1439, Build(w => { w.Write((byte)1); WriteName(w, fullName); w.Write((byte)0); WriteName(w, ""); }));
            That(fixture.Query("SELECT source_account,target_account FROM account_social_requests WHERE source_account='targetrequest'")
                is [["targetrequest", "targetlong"]], "32B/16UTF16完整邀请键精确命中目标账号，不按角色名或截断别名查询");
            byte[] Pending(bool outgoing, string name) => Build(w =>
            {
                w.Write((byte)1); w.Write(outgoing ? (byte)1 : (byte)0); WriteName(w, name);
                WriteName(w, "喇叭邀请"); w.Write(0x7ffeae7fu);
            });
            Refresh(requester, 0, Pending(true, fullName));
            Refresh(target, 0, Pending(false, "TargetRequester"));
            target.Send(1439, Add(fullName)); target.Expect(1, 1439, [0, 3, 0]);
            That(fixture.Query("SELECT count(*) FROM account_social_requests WHERE source_account='targetlong'") is [[0L]],
                "完整长名仍保留同账号拒绝错误3，不为换角喇叭放宽自身好友规则");
        }
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "社交操作重启后保持外键与数据库完整");
        Console.WriteLine($"PASS {Count - before} 玩家社交、名字聊天、资料及重启TCP断言");

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True");
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
    }
    static void PartyInteraction(TcpTestFixture fixture, TcpPeer alpha, TcpPeer beta)
    {
        byte[] Peer(ushort id, byte mode, bool invitation, uint value = 0) => Build(w =>
        {
            w.Write(id); w.Write(mode); w.Write(value);
            if (invitation) w.Write(new byte[mode == 0 ? 6 : 4]);
        });
        byte[] Party(int count, bool removed = false, byte leader = 0, bool membersOnly = false) => Build(w =>
        {
            w.Write((ushort)1); w.Write((ushort)9999); w.Write((ushort)1); w.Write((byte)2); w.Write((byte)10);
            w.Write((byte)0); w.Write(removed ? (byte)3 : membersOnly ? (byte)2 : (byte)0); w.Write((byte)0);
            if (removed) return;
            if (!membersOnly)
            {
                w.Write((byte)0); w.Write((byte)0); WriteName(w, "队伍");
                w.Write((byte)0); w.Write((byte)4); w.Write(new byte[21]); w.Write((byte)7);
            }
            w.Write((byte)count);
            for (byte i = 0; i < count; i++) { w.Write(i); w.Write(i == 0 ? alpha.Id : beta.Id); w.Write(new byte[5]); }
            w.Write((byte)0); w.Write((byte)0); w.Write(leader); w.Write(new byte[7]);
        });
        byte[] Realtime(int count) => Build(w =>
        {
            w.Write((byte)count);
            for (byte i = 0; i < count; i++) { w.Write(i == 0 ? alpha.Id : beta.Id); w.Write((byte)100); w.Write((byte)0); w.Write(i); }
        });
        void SceneInfo(TcpPeer peer)
        {
            peer.Expect(0, 9, Party(2)); peer.Expect(0, 153, Realtime(2));
        }
        void PartyInfo(TcpPeer peer, int count)
        {
            for (int i = 0; i < count; i++) peer.Expect(0, 2);
            peer.Expect(0, 153, Build(w =>
            {
                w.Write((byte)count);
                for (byte i = 0; i < count; i++) { w.Write(i == 0 ? alpha.Id : beta.Id); w.Write((byte)100); w.Write((byte)0); w.Write(i); }
            }));
            var endpoints = peer.Expect(0, 11).Body;
            That(endpoints[0] == count && endpoints.Length == 1 + 22 * count && endpoints[11] == 0x75 && endpoints[12] == 0 && endpoints[22] == 100,
                "队友UDP端点先于名册，端口以网络字节序发送且不覆盖城镇血条为零");
            peer.Expect(0, 9, Party(count));
            peer.Expect(0, 11, endpoints);
            peer.Expect(0, 153, Build(w =>
            {
                w.Write((byte)count);
                for (byte i = 0; i < count; i++) { w.Write(i == 0 ? alpha.Id : beta.Id); w.Write((byte)100); w.Write((byte)0); w.Write(i); }
            }));
            peer.Expect(0, 26, [0]);
        }
        alpha.Send(166); alpha.Expect(0, 153, [0]); alpha.Barrier();
        alpha.Send(2, Convert.FromHexString("051A19ACF81A19ACF80075000200000C000000303065303163356130343163"));
        beta.Send(2, Convert.FromHexString("051A19ACF81A19ACF80075000200000C000000303065303163356130343163"));
        alpha.Barrier(); beta.Barrier();
        alpha.Send(10, Peer(beta.Id, 0, true)); alpha.Expect(1, 10, [1]);
        beta.Expect(0, 2); beta.Expect(0, 7, Build(w => { w.Write(alpha.Id); w.Write((byte)0); w.Write(new byte[14]); }));
        beta.Send(11, Peer(alpha.Id, 0, false)); beta.Expect(1, 11, [1, ..Build(w => w.Write(alpha.Id)), 0]);
        PartyInfo(alpha, 2); PartyInfo(beta, 2);
        alpha.Expect(0, 2); alpha.Expect(0, 8, Peer(beta.Id, 0, false));
        alpha.Barrier(); beta.Barrier();
        alpha.Send(166); alpha.Expect(0, 153, Realtime(2)); alpha.Barrier();
        beta.Send(166); beta.Expect(0, 153, Realtime(2)); beta.Barrier();
        void Transfer(TcpPeer from, byte slot)
        {
            from.Send(121, [slot]); from.Expect(1, 121, [1]);
            foreach (var peer in new[] { alpha, beta })
            {
                peer.Expect(0, 26, [slot]); peer.Expect(0, 9, Party(2, leader: slot, membersOnly: true));
                peer.Expect(0, 153, Realtime(2)); peer.Barrier();
            }
        }
        Transfer(alpha, 1);
        alpha.Send(121, [1]); alpha.Barrier(); beta.Barrier();
        alpha.Send(14, [1]); alpha.Expect(1, 14, [0, 4, 0]); alpha.Barrier(); beta.Barrier();
        Transfer(beta, 0);
        beta.Send(2, Convert.FromHexString("051A19ACF81A19ACF800F5000200000C000000303065303163356130343163"));
        foreach (var peer in new[] { alpha, beta })
        {
            var changed = peer.Expect(0, 11).Body;
            That(changed.Length == 45 && changed[33] == 0xf5 && changed[34] == 0,
                "队员端口变化只刷新实际端点，不重建Actor或队伍名册");
        }
        // 组队状态下从独立房间到公共地图，再离开并返回；每次场景绑定后恢复相同队伍。
        alpha.Send(1426); alpha.SendBarrier(); alpha.Batch((0, 23), (0, 24)); SceneInfo(alpha); alpha.Expect(1, 1347, [1]);
        beta.Barrier();
        beta.Send(1426); beta.SendBarrier(); beta.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22));
        SceneInfo(beta); beta.Expect(1, 1347, [1]);
        alpha.Batch((0, 2), (0, 3), (0, 23), (0, 22)); alpha.Barrier();
        // 对应实机现场：两人先后走门进入同一区域，期间不能发会清队伍槽的NOTI6或重发端点。
        foreach (var step in new[] { (Area: (byte)2, Previous: (byte)0, X: (ushort)1085, Y: (ushort)249),
            (Area: (byte)0, Previous: (byte)2, X: (ushort)629, Y: (ushort)196) })
        {
            alpha.Send(36, Area(38, step.Area, step.X, step.Y, 38, step.Previous, kind: 5));
            alpha.Batch((0, 23), (0, 24)); SceneInfo(alpha); alpha.Barrier();
            alpha.Send(166); alpha.Expect(0, 153, Realtime(2)); alpha.Barrier();
            beta.Expect(0, 23, AreaNotice(alpha.Id, 38, step.Area, step.X, step.Y)); beta.Barrier();
            beta.Send(36, Area(38, step.Area, step.X, step.Y, 38, step.Previous, kind: 5));
            beta.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22)); SceneInfo(beta); beta.Barrier();
            alpha.Batch((0, 2), (0, 3), (0, 23), (0, 22)); alpha.Barrier();
        }
        alpha.Send(36, Area(38, 1, 450, 234, 38, 0)); alpha.SendBarrier();
        alpha.Batch((0, 23), (0, 24), (0, 1379)); SceneInfo(alpha); alpha.Expect(1, 1347, [1]);
        beta.Expect(0, 23, AreaNotice(alpha.Id, 38, 1, 450, 234)); beta.Barrier();
        alpha.Send(1426); alpha.SendBarrier(); alpha.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22));
        SceneInfo(alpha); alpha.Expect(1, 1347, [1]);
        beta.Batch((0, 2), (0, 3), (0, 23), (0, 22)); beta.Barrier();
        alpha.Send(14, [1]); alpha.Expect(1, 14, [1]); PartyInfo(alpha, 1); beta.Expect(0, 9, Party(0, true));
        beta.Expect(0, 9, Party(1));
        alpha.Send(13); alpha.Expect(1, 13, [1]); alpha.Expect(0, 9, Party(0, true));
        beta.Expect(0, 9, Party(0, true));
    }
    static void VerifyPartyDirectory(TcpTestFixture fixture)
    {
        using var leader = Open(fixture, "panela");
        using var observer = Open(fixture, "panelb");
        leader.Create("PanelA"); observer.Create("PanelB");
        foreach (var peer in new[] { leader, observer })
        {
            fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3145)", peer.Id);
            peer.Select(prepared: true);
        }
        leader.Send(1426); leader.Batch((0, 23), (0, 24)); leader.Barrier();
        observer.Send(1426); observer.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22));
        leader.Batch((0, 2), (0, 3), (0, 23), (0, 22)); leader.Barrier(); observer.Barrier();
        leader.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5)); leader.Batch((0, 23), (0, 24));
        observer.Expect(0, 23, AreaNotice(leader.Id, 38, 2, 1085, 249));
        observer.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5));
        observer.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22));
        leader.Batch((0, 2), (0, 3), (0, 23), (0, 22)); leader.Barrier(); observer.Barrier();
        leader.Send(12, Convert.FromHexString("000404000000000500000000000007"));
        byte[] created = OwnInformation();
        That(created.Length == 56 && created[12] == 4 && created[14] == 4 && created[19] == 5 && created[36] == 7 &&
            created[37] == 1 && created[38] == 0 && U16(created, 39) == leader.Id,
            "真实创建按钮请求返回完整预设标题、容量、难度及单人名册，没有伪造通用ACK");
        observer.Expect(0, 9, created);
        // 同屏队长可邀请异区队员；未打开J查询的旁观者也必须先得到被引用的Actor。
        using (var guest = Open(fixture, "panelc"))
        {
            guest.Create("PanelC");
            fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3145)", guest.Id);
            guest.Select(prepared: true); guest.Barrier();
            leader.Send(10, Build(w => { w.Write(guest.Id); w.Write((byte)0); w.Write(0u); w.Write(new byte[6]); }));
            leader.Expect(1, 10, [1]); guest.Expect(0, 2); guest.Expect(0, 7);
            guest.Send(11, Build(w => { w.Write(leader.Id); w.Write((byte)0); w.Write(0u); }));
            guest.Expect(1, 11, [1, ..Build(w => w.Write(leader.Id)), 0]);
            leader.Batch((0, 2), (0, 2), (0, 153), (0, 11));
            byte[] joined = leader.Expect(0, 9).Body;
            leader.Batch((0, 11), (0, 153), (0, 26), (0, 2), (0, 8));
            guest.Batch((0, 2), (0, 2), (0, 153), (0, 11)); guest.Expect(0, 9, joined);
            guest.Batch((0, 11), (0, 153), (0, 26));
            byte[] remoteMember = observer.Expect(0, 2).Body;
            That(remoteMember[0] == 0 && U16(remoteMember, 121) == guest.Id,
                "被动名册也先安装异区队员公开Actor，不创建同屏坐标或实体");
            observer.Expect(0, 9, joined);
            // 本人打开目录时已有异区队友，查询不能再次安装队友Actor并改变队伍成员引用。
            byte[] ownQuery = joined.ToArray(); ownQuery[2] = 1; ownQuery[3] = 0;
            guest.Send(1404, Query(guest.Id, kind: 0)); guest.Expect(0, 9, ownQuery); guest.Barrier();
            guest.Send(13); guest.Expect(1, 13, [1]); guest.Expect(0, 9);
            Bytes(OwnInformation(), created, "异区队员离开后保留原队长及设置");
            observer.Expect(0, 9, created);
            byte[] remainingQuery = created.ToArray(); remainingQuery[2] = 1; remainingQuery[3] = 0;
            var formerLeader = guest.Expect(0, 2).Body;
            That(U16(formerLeader, 121) == leader.Id, "离队后查询别人的异区队伍仍补齐公开资料");
            guest.Expect(0, 9, remainingQuery);
            leader.Barrier(); guest.Barrier(); observer.Barrier();
        }
        observer.Send(12, Convert.FromHexString("010504000000000500000000000007"));
        observer.Expect(1, 12, [0, 21, 0]);
        leader.Send(12, Build(w => { w.Write((byte)1); w.Write((byte)0); WriteName(w, "一起冒险");
            w.Write((byte)3); w.Write(1234u); w.Write((byte)2); w.Write((ushort)0); w.Write((byte)0);
            w.Write((byte)0); w.Write((ushort)0); w.Write((byte)7); }));
        byte[] edited = OwnInformation();
        That(U16(edited, 4) == U16(created, 4) && edited[12] == 0 && U32(edited, 13) == 8 &&
            DecodeName(edited, 17, 8) == "一起冒险" && edited[26] == 3 && U32(edited, 42) == 1234,
            "修改队伍保留原ID，完整回放自定义标题、人数及副本设置");
        observer.Expect(0, 9, edited);
        leader.Send(12, Build(w => { w.Write((byte)1); w.Write((byte)5); w.Write((byte)3); w.Write(1234u);
            w.Write((byte)2); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)1); w.Write((ushort)0); }));
        edited = OwnInformation();
        That(edited[12] == 5 && edited[14] == 3 && edited[23] == 1 && U32(edited, 30) == 1234 && edited[36] == 7,
            "原生快捷标题更新使用14字节布局，保留原招募选项并正确回填目标副本");
        observer.Expect(0, 9, edited);
        observer.Send(7); observer.Batch((0, 6), (1, 7), (0, 2)); leader.Expect(0, 6);
        observer.Select(prepared: true);
        observer.Send(1426); observer.Batch((0, 23), (0, 24));
        observer.Send(36, Area(38, 2, 1085, 249, 38, 0, kind: 5));
        // 目录由独立投影发布，可穿插在切图通知中；仍逐包核对切图顺序及唯一完整名册。
        bool directoryReceived = false;
        foreach (ushort opcode in new ushort[] { 23, 24, 2, 3, 23, 22 })
        {
            var packet = observer.Read();
            if (packet.Kind == 0 && packet.Opcode == 9)
            {
                That(!directoryReceived, "回城名册只刷新一次");
                Bytes(packet.Body, edited, "回城名册保留完整队伍设置");
                directoryReceived = true;
                packet = observer.Read();
            }
            That(packet.Kind == 0 && packet.Opcode == opcode,
                $"回城切图包序：期望0/{opcode}，实际{packet.Kind}/{packet.Opcode}");
        }
        leader.Batch((0, 2), (0, 3), (0, 23), (0, 22));
        if (!directoryReceived) observer.Expect(0, 9, edited);
        leader.Send(15, new byte[4]); leader.Batch((0, 6), (1, 15), (0, 3), (0, 1536), (0, 27));
        observer.Expect(0, 6);
        var hidden = observer.Expect(0, 9).Body;
        That(hidden.Length == 11 && hidden[9] == 3 && U16(hidden, 4) == U16(created, 4),
            "队长打开副本选择后，其他玩家移除其城镇招募行");
        leader.Send(132); leader.Batch((1, 132), (0, 3), (0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22));
        observer.Batch((0, 2), (0, 3), (0, 23), (0, 22));
        observer.Expect(0, 9, edited);
        // 真实J面板通过1404查询；增量名册正确不代表列表已经绘制。
        byte[] Query(ushort actor, string keyword = "", ushort page = 0, byte kind = 1) => Build(w =>
        {
            w.Write(actor); w.Write((byte)1); w.Write(new byte[10]); w.Write((ushort)10); w.Write(actor); w.Write((byte)0);
            w.Write(page); w.Write((byte)1); w.Write((byte)0); w.Write((byte)0); w.Write((byte)1); w.Write(kind); w.Write((byte)7);
            var text = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(keyword);
            w.Write(text); w.Write(new byte[32 - text.Length]);
        });
        byte[] queryResult = edited.ToArray(); queryResult[2] = 1; queryResult[3] = 0;
        observer.Send(1404, Query(observer.Id)); observer.Expect(0, 9, queryResult);
        observer.Send(1404, Query(observer.Id, "无匹配")); observer.Expect(0, 9, [0, 0, 0, 0]);
        observer.Send(1404, Query(observer.Id, page: 100)); observer.Expect(0, 9, [0, 0, 1, 0]);
        observer.Send(1404, Query(observer.Id, kind: 0)); observer.Expect(0, 9, [0, 0, 0, 0]);
        observer.Send(1404, Query(leader.Id)); observer.Barrier();
        observer.NoFrame(100, "查询不能借路由头查询其他角色", (0, 9));
        observer.Send(1404, Query(observer.Id, "PanelA")); observer.Expect(0, 9, queryResult);
        observer.Send(36, Area(38, 0, 629, 196, 38, 2));
        observer.Batch((0, 23), (0, 24)); leader.Expect(0, 23, AreaNotice(observer.Id, 38, 0, 629, 196));
        byte[] remoteActor = RemoteDirectory();
        int repeatedDirectories = 0;
        bool awaitingDirectory = false;
        observer.Barrier(packet =>
        {
            if (!awaitingDirectory)
            {
                That(++repeatedDirectories <= 1 && packet is { Kind: 0, Opcode: 2 },
                    "切区屏障仅允许一组已发送的同一异区队伍快照，先Actor后名册");
                Bytes(packet.Body, remoteActor, "重复异区Actor与本轮已校验队长公开资料逐字节一致");
            }
            else
            {
                That(packet is { Kind: 0, Opcode: 9 }, "重复异区Actor后必须紧接当前名册");
                Bytes(packet.Body, queryResult, "重复异区名册逐字节对应独立构造的当前队伍查询结果");
            }
            awaitingDirectory = !awaitingDirectory;
        });
        That(!awaitingDirectory, "切区屏障前额外目录必须成对完成");
        observer.NoFrame(100, "异区队伍资料不能生成城镇实体、位置或移动", (0, 3), (0, 22), (0, 23), (0, 24));
        // 使用无队伍客户端实收58B默认请求，路由字段仍不参与授权。
        byte[] nativeQuery = Convert.FromHexString("030001000000000000000000001C0003000000000100000106070000000000000000000000000000000000000000000000000000000000000000");
        nativeQuery[0] = nativeQuery[15] = (byte)observer.Id;
        nativeQuery[1] = nativeQuery[16] = (byte)(observer.Id >> 8);
        observer.Send(1404, nativeQuery); observer.Expect(0, 9, [0, 0, 0, 0]);
        leader.Send(12, Convert.FromHexString("010404000000000500000000000007"));
        edited = OwnInformation();
        queryResult = edited.ToArray(); queryResult[2] = 1; queryResult[3] = 0;
        RemoteDirectory();
        // 切区后的查询、异区创建与队长切区都必须继续发现当前队伍。
        leader.Send(13); leader.Expect(1, 13, [1]); leader.Expect(0, 9);
        observer.Expect(0, 9, [0, 0, 0, 0]);
        leader.Send(12, Convert.FromHexString("000404000000000500000000000007"));
        edited = OwnInformation();
        queryResult = edited.ToArray(); queryResult[2] = 1; queryResult[3] = 0;
        RemoteDirectory();
        leader.Send(36, Area(38, 0, 629, 196, 38, 2));
        leader.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22)); OwnSceneInformation();
        observer.Batch((0, 2), (0, 3), (0, 23), (0, 22)); observer.Expect(0, 9, queryResult);
        leader.Send(36, Area(38, 1, 450, 234, 38, 0));
        leader.Batch((0, 23), (0, 24), (0, 1379)); OwnSceneInformation();
        // 前次同区目录异步发布可能晚于队长本人的切区响应，仅接受相同查询结果的一次重放。
        var departure = observer.Read();
        if (departure is { Kind: 0, Opcode: 9 })
        {
            Bytes(departure.Body, queryResult, "离区前重复同区目录必须逐字节等于当前队伍快照");
            departure = observer.Read();
        }
        That(departure is { Kind: 0, Opcode: 23 }, "至多一次相同目录后必须收到队长离区通知");
        Bytes(departure.Body, AreaNotice(leader.Id, 38, 1, 450, 234), "离区通知仍严格匹配队长与目的区域");
        RemoteDirectory();
        leader.Send(1404, Query(leader.Id, kind: 6)); leader.Expect(0, 9, queryResult);
        leader.Send(13); leader.Expect(1, 13, [1]);
        byte[] removed = leader.Expect(0, 9).Body;
        That(removed.Length == 11 && removed[9] == 3 && U16(removed, 4) == U16(edited, 4), "退出最后一人删除真实队伍");
        // 异步目录可能已投递切区后的同一快照；逐条核对后仍必须收到退队空列表。
        var last = observer.Read();
        for (int pending = 0; pending < 4 && last is { Kind: 0, Opcode: 2 }; pending++)
        {
            That(last.Body[0] == 0 && U16(last.Body, 121) == leader.Id, "退队前已发送的目录资料仍属于同一队长");
            observer.Expect(0, 9, queryResult);
            last = observer.Read();
        }
        That(last is { Kind: 0, Opcode: 9 }, "目录在有界已发送快照之后收到退队结果");
        Bytes(last.Body, [0, 0, 0, 0], "退队后队伍列表最终为空");
        leader.Expect(0, 9, [0, 0, 0, 0]);
        leader.Barrier(); observer.Barrier();

        byte[] RemoteDirectory()
        {
            var remote = observer.Expect(0, 2).Body;
            That(remote[0] == 0 && U16(remote, 1) == 1 && U16(remote, 121) == leader.Id,
                "异区名册前先安装当前队长公开资料，不能引用缺失Actor或安装本人资料");
            observer.Expect(0, 9, queryResult);
            return remote;
        }

        void OwnSceneInformation()
        {
            leader.Expect(0, 9, edited); leader.Expect(0, 153, Build(w =>
            {
                w.Write((byte)1); w.Write(leader.Id); w.Write((byte)100); w.Write((byte)0); w.Write((byte)0);
            }));
        }

        byte[] OwnInformation()
        {
            leader.Batch((0, 2), (0, 153), (0, 11));
            byte[] body = leader.Expect(0, 9).Body;
            leader.Batch((0, 11), (0, 153), (0, 26));
            return body;
        }
    }
    static byte[] Refresh(TcpPeer peer, int count, byte[]? requests = null, bool accountOnline = true,
        bool characterOnline = true, string? accountAdventure = null, string? accountCharacter = null)
    {
        var body = peer.Expect(0, 273).Body;
        That(U32(body, 0) == 0 && U32(body, 4) == count, "好友全量subcommand及真实行数");
        for (int i = 0; i < count; i++)
        {
            var online = peer.Expect(0, 274).Body;
            That(U16(online) == (characterOnline ? 10 : 0), "角色好友按当前角色在线状态下发真实频道，换角不能误判原角色在线");
        }
        VerifyAccountPresence(peer.Expect(0, 1376).Body, accountOnline, accountAdventure, accountCharacter);
        var receivedRequests = peer.Expect(0, 1377).Body;
        if (requests is not null) That(receivedRequests.AsSpan().SequenceEqual(requests), "申请方向、发起方冒险团、留言和时间按原生通知中心协议投递");
        return body;
    }
    static void VerifyAccountPresence(byte[] body, bool online, string? expectedAdventure = null, string? expectedCharacter = null)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        string Name() => DfoServer.Game.Characters.CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
        int count = reader.ReadByte();
        if (expectedAdventure is not null || expectedCharacter is not null)
            That(count == 1, "同一账号换角、离线及重登始终只有一条冒险团好友");
        for (int i = 0; i < count; i++)
        {
            ushort homeServer = reader.ReadUInt16(); byte channelServer = reader.ReadByte();
            That(homeServer == 2 && channelServer == 2, "不同角色、换角和离线均使用相同真实大区，不能写ActorID");
            string character = Name(); reader.ReadBytes(3);
            ushort channel = reader.ReadUInt16(); string adventure = Name(); Name();
            reader.ReadUInt32(); byte unavailable = reader.ReadByte(); reader.ReadBytes(2);
            That(adventure.Length != 0 && (online ? channel == 10 && character.Length != 0 && unavailable == 0 : channel == 0),
                "冒险团在线绘制要求真实非零频道、角色名及不可用标记0，离线由频道0表示");
            if (expectedAdventure is not null) That(adventure == expectedAdventure, "冒险团唯一键不随登录角色变化");
            if (expectedCharacter is not null) That(character == expectedCharacter, "登录角色字段显示当前角色，不与冒险团昵称互换");
        }
        That(stream.Position == stream.Length, "冒险团好友行完整消费，不以仅验证数量掩盖尾字段错误");
    }
    static byte[] AreaNotice(ushort id, byte town, byte area, ushort x, ushort y) => Build(w =>
    {
        w.Write(id); w.Write(town); w.Write(area); w.Write(x); w.Write(y); w.Write((byte)0); w.Write((byte)3);
    });
    static byte[] BlockBody(string name, string memo) => Build(w => { WriteName(w, name); w.Write((byte)2); WriteName(w, memo); });
    static void WriteName(BinaryWriter writer, string value)
    {
        byte[] bytes = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
}
