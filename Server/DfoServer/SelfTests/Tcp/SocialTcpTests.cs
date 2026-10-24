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
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "social"), resources: resources);
        await fixture.StartAsync();
        ushort alphaId;
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
            alpha.Send(120); alpha.Expect(1, 120, [1, 0]);
            alpha.Send(290, Build(w => { w.Write(beta.Id); w.Write((byte)2); WriteName(w, "SocialB"); }));
            alpha.Expect(1, 290, [1]);
            var friends = Refresh(alpha, 1);
            That(U32(friends, 4) == 1 && fixture.Query("SELECT owner_id,target_id,kind FROM character_social_links") is [[long, long, 0L]],
                "290提交后回真实全量好友且只建立单向边");
            beta.Barrier();
            byte[] Named(byte mode, string value) => Build(w =>
            {
                w.Write(mode); w.Write((ushort)1); w.Write(0u); WriteName(w, value); WriteName(w, "SocialB"); w.Write((byte)2);
            });
            alpha.Send(17, Named(36, "一对一"));
            beta.Expect(0, 65, Build(w => { w.Write((byte)36); w.Write((byte)0); WriteName(w, "SocialA"); w.Write((byte)2);
                WriteName(w, "一对一"); WriteName(w, "SocialB"); }));
            alpha.Send(17, Named(1, "悄悄话"));
            beta.Expect(0, 65, Build(w => { w.Write((byte)1); w.Write((byte)0); WriteName(w, "SocialA"); w.Write((byte)2); WriteName(w, "悄悄话"); }));
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
            Execute("UPDATE account_adventures SET name=CASE account WHEN 'sociala' THEN 'TeamA' ELSE 'TeamB' END,name_initialized=1");
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
            using (var owner = owners.Acquire("socialb", () => { }))
                repository.Create(owner, CharacterCreation.Prepare("SocialBAlt", resources.Characters.Get(1),
                    resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            ushort original = beta.Id;
            foreach (ushort slot in new ushort[] { 1, 0 })
            {
                beta.Send(7);
                beta.Expect(1, 7, [1]); beta.Expect(0, 2);
                Refresh(alpha, 1, accountOnline: false, characterOnline: false, accountAdventure: "TeamB");
                beta.Select(slot);
                Refresh(alpha, 1, characterOnline: slot == 0, accountAdventure: "TeamB",
                    accountCharacter: slot == 0 ? "SocialB" : "SocialBAlt");
                That(slot == 0 ? beta.Id == original : beta.Id != original,
                    "换角使用真实不同Actor，冒险团好友仍只覆盖TeamB这一行");
                beta.Barrier();
            }
            That(fixture.Query("SELECT count(*) FROM account_social_links") is [[2L]],
                "换角不新增账号好友边，双方始终各一条冒险团关系");
        }
        await fixture.StopAsync(); await fixture.StartAsync("social-reopen", isolatedProcess: true);
        using (var alpha = Open(fixture, "sociala"))
        {
            alpha.Select();
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
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "社交操作重启后保持外键与数据库完整");
        Console.WriteLine($"PASS {Count - before} 玩家社交、名字聊天、资料及重启TCP断言");

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True");
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
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
            reader.ReadUInt16(); reader.ReadByte();
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
    static byte[] BlockBody(string name, string memo) => Build(w => { WriteName(w, name); w.Write((byte)2); WriteName(w, memo); });
    static void WriteName(BinaryWriter writer, string value)
    {
        byte[] bytes = DfoServer.Game.Characters.CharacterName.Encoding.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
}
