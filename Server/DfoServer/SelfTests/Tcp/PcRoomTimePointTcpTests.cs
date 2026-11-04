using System.IO.Compression;
using System.Net.Sockets;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PcRoomTimePointTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var definition = ServerEventService.Definition(228);
        That(definition.Name == "累积在线得好礼" && definition.DefaultState == "默认开启" && !definition.SupportsSchedule,
            "GM目录按真实活动228注册名称、默认开启和开关政策");
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-28T20:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "pcroom-timepoint"),
            gm: true, resources: resources, clock: clock);
        string account = fixture.Options.DevelopmentAccount;
        var repository = new CharacterRepository(fixture.Database);
        ushort firstId, secondId;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var candidate = CharacterCreation.Prepare("OnlineGiftA", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            firstId = repository.Create(owner, candidate).Id;
            secondId = repository.Create(owner, candidate with { Name = "OnlineGiftB" }).Id;
        }
        await fixture.StartAsync();
        bool enabled = false;
        byte[]? progress = null;
        int alarms = 0, eventTables = 0;
        using var original = Open(fixture);
        TcpPeer client = original;
        original.Select();
        Request();
        State(0, 0, 0, 0, "隔离夹具显式关闭后仍可查询真实零进度");
        clock.Advance(TimeSpan.FromMinutes(5));
        Request(1, 0xff);
        State(0, 0, 0, 0, "关闭时在线和领奖都不产生时长、领取位或邮件");
        That(MailCount() == 0, "关闭状态没有活动邮件或附件");

        Gm("event on 228", true);
        Request();
        That(eventTables > 0, "GM开启后在同连接刷新完整extra与active集合");
        State(0, 0, 0, 0, "活动刚开启不补算此前关闭的五分钟");
        clock.Advance(TimeSpan.FromMinutes(30));
        Request();
        State(1800, 0, 0, 0, "真实单调时钟推进30分钟才达到当前PVF首档");
        That(resources.ItemResources.Items.Get(490014060).Expiration is { AbsoluteTime: 0, Days: 0, DailyDelete: 0, WeeklyDeleteDay: null },
            "当前客户端首档礼盒已无期限声明，不能沿用旧PVF的过期夹具");
        Request(1, 0xff);
        State(1800, 0, 1, 0, "达到真实阈值后发首档奖励并同步领取位");
        That(MailCount() == 1 && alarms > 0, "首档只创建一封实际系统奖励邮件并发送99提醒");
        var firstAttachment = fixture.Query("""
            SELECT m.recipient_id,a.item_uid,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value'),
              json_extract(i.item_core,'$.ExpireTime'),m.system_mail,m.expires_at
            FROM mailbox_messages m JOIN mailbox_attachments a ON a.mail_id=m.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid
            WHERE m.delivery_key LIKE 'online228:%'
            """);
        That(firstAttachment is [[long recipient,long uid,490014060L,1L,long expiry,1L,DBNull]] &&
            recipient == firstId && uid > 0 && expiry == 0,
            "当前PVF首档宠物饲料礼盒以真实UID、实际数量和资源声明的无期限进入永久系统邮件");
        string mailBefore = MailSnapshot();
        int alarmBefore = alarms;
        Request(1, 0xff);
        That(MailSnapshot() == mailBefore && alarms == alarmBefore,
            "完全相同的日领取请求回放原领取事实，不重发邮件、UID或提醒");

        Gm("event off 228", false);
        Request();
        clock.Advance(TimeSpan.FromMinutes(10));
        Request();
        State(1800, 0, 1, 0, "关闭的十分钟不累计且保留已领进度");
        Gm("event on 228", true);
        Request();
        clock.Advance(TimeSpan.FromMinutes(10));
        Request();
        State(2400, 0, 1, 0, "重新开启继续原账号40分钟进度，不重新清零");
        client.Send(7); client.Expect(1, 7, [1]); client.Expect(0, 2);
        client.ExpectedOpenEvents = [ServerEventService.NolanVisibilityId, ServerEventService.MileageShopId, 228];
        client.ExpectedActiveEvents = [2395, 228];
        client.Select(1);
        Request();
        State(2400, 0, 1, 0, "同连接换角继承账号时长与首档已领位");
        Request(1, 0xff);
        That(MailSnapshot() == mailBefore, "换角重复领取不能把账号奖励重新发给另一个角色");

        clock.Advance(TimeSpan.FromSeconds(7));
        using var replacement = Open(fixture);
        // 七秒推进可在接管前已唤醒旧连接：只容纳该次完整561，然后仍必须真的关闭。
        int oldProgress = 0;
        try
        {
            while (true)
            {
                var tail = original.Read();
                That(++oldProgress <= 1 && tail is { Kind: 0, Opcode: 561 }, "接管旧连接最多残留一帧已发送的进度，不跳过其它通知");
                Bytes(tail.Body, Build(writer => { writer.Write(2407u); writer.Write(0u); writer.Write((byte)1);
                    writer.Write(0u); writer.Write(0u); }), "接管前异步561必须精确对应旧段2407秒及原领取位");
            }
        }
        catch (EndOfStreamException) { That(true, "接管后旧连接到达真实EOF"); }
        catch (IOException error) when (error.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        { That(true, "接管后旧连接明确重置"); }
        replacement.ExpectedOpenEvents = [ServerEventService.NolanVisibilityId, ServerEventService.MileageShopId, 228];
        replacement.ExpectedActiveEvents = [2395, 228];
        replacement.Select(1);
        client = replacement;
        Request();
        State(2407, 0, 1, 0, "同账号接管前旧lease提交最后七秒且新连接继承进度");
        clock.Advance(TimeSpan.FromSeconds(3));
        Request();
        State(2410, 0, 1, 0, "旧连接结束后只由新段追加三秒，不重计旧段或丢失尾水位");

        // 累计奖励的五个历史合格日明确用SQL准备，不冒称等待了五天。
        for (int days = 1; days <= 5; days++)
        {
            int day = int.Parse(new DateOnly(2026, 10, 28).AddDays(-days).ToString("yyyyMMdd"));
            fixture.Sql("INSERT INTO account_pcroom_timepoint_days(account,day,elapsed_ms,qualified) VALUES($p0,$p1,7200000,1)", account, day);
        }
        Request(0x10, 0);
        State(2410, 5, 1, 1, "实际累计请求使用准备的五个合格日达到当前PVF首档");
        That(MailCount() == 2 && fixture.Query("""
            SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value'),i.item_uid
            FROM account_pcroom_timepoint_claims c JOIN mailbox_attachments a ON a.mail_id=c.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE c.account=$p0 AND c.kind=1 AND c.reward_index=0
            """, account) is [[490014067L,3L,long periodUid]] && periodUid > 0,
            "累计首档按当前资源发苍穹碎片礼盒三件且有独立真实UID");
        mailBefore = MailSnapshot();
        Request(0x10, 0);
        That(MailSnapshot() == mailBefore, "累计首档重复请求不产生额外邮件或UID");

        var boundary = DateTimeOffset.Parse("2026-10-28T22:00:00Z");
        int remaining = checked((int)(boundary - clock.GetUtcNow()).TotalSeconds);
        clock.Advance(TimeSpan.FromSeconds(remaining - 1));
        Request();
        State(checked((uint)(2410 + remaining - 1)), 5, 1, 1, "CST06前保留当前服务日的真实累计秒和领取位");
        clock.Advance(TimeSpan.FromSeconds(2));
        Request();
        State(1, 5, 0, 1, "CST06仅新日时长与每日领取归零，累计合格日和累计领取保留");
        That(fixture.Query("SELECT elapsed_ms FROM account_pcroom_timepoint_days WHERE account=$p0 AND day=20261028", account)
            is [[long oldDay]] && oldDay == (2410L + remaining) * 1000 &&
            fixture.Query("SELECT elapsed_ms FROM account_pcroom_timepoint_days WHERE account=$p0 AND day=20261029", account) is [[1000L]],
            "跨日真实区间按边界各入一次，旧日最后一秒和新日第一秒均不遗漏");
        That(MailSnapshot() == mailBefore, "跨日不重发任何旧奖励");

        clock.Advance(TimeSpan.FromSeconds(1798));
        Request(1, 0xff);
        State(1799, 5, 0, 1, "首档差一秒时领奖不成功、不补时长");
        That(MailSnapshot() == mailBefore, "未达阈值不发邮件或占领取位");
        clock.Advance(TimeSpan.FromSeconds(1));
        Request(1, 0xff);
        State(1800, 5, 1, 1, "新服务日可独立领取首档");
        clock.Advance(TimeSpan.FromSeconds(1800));
        Request(2, 0xff);
        State(3600, 5, 3, 1, "每日第二档60分钟正确领取");
        clock.Advance(TimeSpan.FromSeconds(3600));
        Request();
        State(7200, 6, 3, 1, "满120分钟无需领取最后两档也记合格日，未自动发奖");
        Request(8, 0xff);
        State(7200, 6, 11, 1, "每日第四档可先于第三档领取");
        Request(4, 0xff);
        State(7200, 6, 15, 1, "每日四档全部领取且领取位互不覆盖");

        // 补齐十九个历史合格日；今天第20天由上面的真实计时达到，不用SQL伪造今天资格。
        for (int days = 6; days <= 19; days++)
        {
            int day = int.Parse(new DateOnly(2026, 10, 28).AddDays(-days).ToString("yyyyMMdd"));
            fixture.Sql("INSERT INTO account_pcroom_timepoint_days(account,day,elapsed_ms,qualified) VALUES($p0,$p1,7200000,1)", account, day);
        }
        Request(0x10, 3);
        State(7200, 20, 15, 9, "累计20天档可独立先领，首档事实保留");
        Request(0x10, 1);
        State(7200, 20, 15, 11, "累计10天档正确领取");
        Request(0x10, 2);
        State(7200, 20, 15, 15, "累计15天档领取后四档全满");
        var awards = fixture.Query("""
            SELECT kind,reward_index,item_id,item_count FROM account_pcroom_timepoint_claims
            WHERE account=$p0 AND (kind=1 OR day=20261029) ORDER BY kind,reward_index
            """, account);
        That(JsonSerializer.Serialize(awards) == JsonSerializer.Serialize(new long[][]
            { [0,0,490014060,1], [0,1,490014061,1], [0,2,490014063,2], [0,3,490014064,1],
              [1,0,490014067,3], [1,1,490014068,1], [1,2,490014069,1], [1,3,490014173,1] }),
            "实际八档领取事实逐项匹配S5A9当前PVF的物品与数量");
        mailBefore = MailSnapshot();
        alarmBefore = alarms;
        foreach (byte type in new byte[] { 1, 2, 4, 8 }) Request(type, 0xff);
        for (byte index = 0; index < 4; index++) Request(0x10, index);
        That(MailCount() == 9 && MailSnapshot() == mailBefore && alarms == alarmBefore,
            "八档全部重复请求均不新增邮件、物理UID或提醒");

        client.Send(96); client.Expect(1, 96, [1, 0, 0]);
        var grants = MailboxTcpTests.ReadList(client.Expect(0, 97).Body).Summaries.Where(row => row.ItemId != 0).ToArray();
        That(grants.Length == 8 && grants.Select(row => row.ItemId).Order().SequenceEqual(
            new[] { 490014060, 490014061, 490014063, 490014064, 490014067, 490014068, 490014069, 490014173 }),
            "当前领奖角色邮箱包含完整八档真实附件");
        var attachmentUids = fixture.Query("""
            SELECT i.item_uid FROM account_pcroom_timepoint_claims c
            JOIN mailbox_attachments a ON a.mail_id=c.mail_id JOIN character_inventory_items i ON i.item_uid=a.item_uid
            WHERE c.account=$p0 AND c.recipient_id=$p1 ORDER BY i.item_uid
            """, account, secondId).Select(row => (long)row[0]!).ToArray();
        client.Send(95, Build(w => { w.Write(grants.Length); foreach (var row in grants) w.Write(row.ClaimId); }));
        client.Expect(1, 95, Build(w =>
        {
            w.Write((byte)1); w.Write(grants.Length);
            foreach (var row in grants) { w.Write(row.MailId); w.Write(row.ClaimId); w.Write(0); }
        }));
        var delta = client.Expect(0, 14).Body;
        That(delta[0] == 0 && U16(delta, 1) == 8, "实际C95批量取件发布八个Main槽位增量");
        var owned = fixture.Query("""
            SELECT i.item_uid FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
            WHERE s.character_id=$p0 AND s.list_type=0
              AND json_extract(item_core,'$.ItemId') IN(490014060,490014061,490014063,490014064,490014067,490014068,490014069,490014173)
            ORDER BY i.item_uid
            """, secondId).Select(row => (long)row[0]!).ToArray();
        That(owned.SequenceEqual(attachmentUids), "八个原物理UID随邮箱取件进入角色背包，不重建或遗漏奖励");
        Request(8, 0xff);
        Request(0x10, 3);
        State(7200, 20, 15, 15, "邮件取件后每日和累计领取事实仍保留");
        That(MailCount() == 9, "取走附件后重复活动领奖不重发");
        client.Send(3, [0]); client.Expect(1, 3, [1]); client.Closed();
        await fixture.StopAsync();
        clock.Advance(TimeSpan.FromDays(2));
        await fixture.StartAsync("reopened");
        using var reopened = Open(fixture);
        reopened.ExpectedOpenEvents = [ServerEventService.NolanVisibilityId, ServerEventService.MileageShopId, 228];
        reopened.ExpectedActiveEvents = [2395, 228];
        reopened.Select(1);
        client = reopened;
        Request();
        State(0, 20, 0, 15, "Runtime重开沿原库恢复账号事实与活动开关，离线两天不补时长或合格日");
        Request(0x10, 3);
        That(MailCount() == 9, "重开并重登后累计奖仍不可重复领取");
        client.Send(3, [0]); client.Expect(1, 3, [1]); client.Closed();
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "真实TCP开关、领取、换角接管与跨日后数据库完整");

        void Gm(string command, bool next)
        {
            enabled = next;
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out string reply) && reply.StartsWith("OK "),
                "GM执行" + command);
        }
        void Request(byte type = 0, byte index = 0xff)
        {
            progress = null;
            client.Send(632, [type, index]);
            foreach (var packet in PartyDungeonTcpTests.Drain(client)) Observe(packet);
            foreach (var packet in PartyDungeonTcpTests.Drain(client)) Observe(packet);
            That(progress is not null, "每次实际632查询或领取收到完整561，不伪造CMD应答");
        }
        void Observe(WirePacket packet)
        {
            That(packet.Kind == 0, "累计在线交互只收到已登记通知，632没有伪造ACK");
            switch (packet.Opcode)
            {
                case 561:
                    That(packet.Body.Length == 17 && U32(packet.Body, 9) == 0, "561正文固定17B且真实四档视觉组为0");
                    progress = packet.Body;
                    break;
                case 108:
                    CheckEvents(packet, enabled);
                    eventTables++;
                    break;
                case 706:
                    Bytes(packet.Body, client.MileageLimits, "228开关保留完整积分商城限购通知");
                    break;
                case 99:
                    Bytes(packet.Body, [1, 0], "实际奖励邮件使用完整99提醒");
                    That(MailCount() > 0, "邮件提醒对应已经提交的真实活动邮件");
                    alarms++;
                    break;
                case 541:
                    var used = DailyBoosterProjectionTests.Decode(packet.Body);
                    That(used.Count > 0 && used.Values.All(value => value == 0) && used.Keys.All(client.DailyBoosterUsed.ContainsKey),
                        "测试角色未用每日礼盒，跨日原有541只恢复合法名册零次数");
                    break;
                default:
                    client.ValidateServiceDayNotice(packet);
                    break;
            }
        }
        void State(uint seconds, uint days, byte daily, uint period, string message) =>
            Bytes(progress!, Build(writer => { writer.Write(seconds); writer.Write(days); writer.Write(daily); writer.Write(0u); writer.Write(period); }), message);
        long MailCount() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'online228:%'").Single()[0]);
        string MailSnapshot() => JsonSerializer.Serialize(fixture.Query("""
            SELECT m.mail_id,m.recipient_id,m.state,a.attachment_id,a.item_uid,i.item_core
            FROM mailbox_messages m JOIN mailbox_attachments a ON a.mail_id=m.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid
            WHERE m.delivery_key LIKE 'online228:%' ORDER BY m.mail_id,a.ordinal
            """));
    }

    static void CheckEvents(WirePacket packet, bool enabled)
    {
        That(packet.Protected, "热开关EVENT_INFO保持保护压缩帧");
        using var compressed = new MemoryStream(packet.Body);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        ushort[] extra = enabled ? [2037, 2378, 2395, 2532, 266, 228] : [2037, 2378, 2395, 2532, 266];
        ushort[] active = enabled ? [2037, 2395, 228] : [2037, 2395];
        byte[] expected = Build(writer =>
        {
            writer.Write(checked((ushort)extra.Length));
            foreach (ushort id in extra)
            { writer.Write(id); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0); }
            writer.Write(checked((byte)active.Length));
            foreach (ushort id in active)
            { writer.Write(id); writer.Write(id == 2037 ? 8u : 0u); for (int index = 1; index < 12; index++) writer.Write(0u); }
        });
        byte[] bounded = new byte[32768];
        int length = 0;
        while (length < bounded.Length)
        {
            int read = zlib.Read(bounded, length, bounded.Length - length);
            if (read == 0) break;
            length += read;
        }
        That(zlib.ReadByte() == -1, "活动整表解压正文有界且完整");
        byte[] actual = bounded[..length];
        if (U16(actual, 0) == extra.Length + 1)
            CashShopTcpTests.AssertPromotionNativeEvents(actual, (long?)0, (bool?)false,
                enabled ? [2532, 266, 228] : [2532, 266], activeEvents: enabled ? [2395, 228] : [2395]);
        else
            Bytes(actual, expected, "228同时进入extra与active全零12字段，关闭只移除228并保留全部原活动");
    }
}
