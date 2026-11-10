using System.IO.Compression;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DailyAttendanceTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-03T04:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "attendance2370"), gm: true, resources: resources, clock: clock);
        string account = fixture.Options.DevelopmentAccount;
        var repository = new CharacterRepository(fixture.Database);
        ushort id;
        using (var owner = new AccountOwners().Acquire(account, () => { }))
            id = repository.Create(owner, CharacterCreation.Prepare("AttendTcp", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get)).Id;
        await fixture.StartAsync();
        using var original = Open(fixture);
        TcpPeer client = original;
        client.Select();
        byte[] nativeBody = [0x10, 0x05, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0x01, 0x02, 0x03, 0x04, 0x05];
        Request(false);
        That(fixture.Query("SELECT count(*) FROM account_daily_attendance_claims") is [[0L]], "关闭活动实际请求无奖励");
        Gm(true);
        Bytes(Request(true), State(0, 0, 0, 0, 0), "开启后真实1296（13B opaque正文）返回37B零进度");
        // 未满7天（6天）时不可领取首档累计奖励。
        for (int index = 0; index < 6; index++)
            fixture.Sql("""
                INSERT INTO account_daily_attendance_claims(account,kind,reward_index,day,recipient_id,mail_id,item_id,item_count,claimed_at)
                VALUES($p0,0,$p1,20261101,$p2,999,490013043,2,1)
                """, account, index, id);
        Bytes(Request(true), State(6, 0, 0, 0, 0), "未满7天不可领取累计奖励");
        That(fixture.Query("SELECT count(*) FROM account_daily_attendance_claims WHERE account=$p0 AND kind=1", account) is [[0L]],
            "未达7天门槛不写入累计领取记录或邮件");
        // 准备第7次历史签到；累计领取走原生13B opaque CMD 1296。
        fixture.Sql("""
            INSERT INTO account_daily_attendance_claims(account,kind,reward_index,day,recipient_id,mail_id,item_id,item_count,claimed_at)
            VALUES($p0,0,6,20261101,$p1,999,490013043,2,1)
            """, account, id);
        Bytes(Request(true), State(7, 2, 0, 0, 0), "原生13B请求领取已达7次的首档累计奖励");
        That(fixture.Query("""
            SELECT c.item_id,c.item_count,i.item_uid,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value')
            FROM account_daily_attendance_claims c JOIN mailbox_attachments a ON a.mail_id=c.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE c.account=$p0 AND c.kind=1 AND c.reward_index=0
            """, account) is [[490013064L,1L,long uid7,490013064L,1L]] && uid7 > 0,
            "当前PVF累计7天档以真实UID和数量进入邮箱");
        Request(true);
        Bytes(Request(true, []), State(7, 2, 0, 0, 0), "兼容空请求且已领7天档位不重复发放");
        That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'attendance2370:%'") is [[1L]],
            "重复13B与空请求不重复发放已领7天累计奖励");
        client.Send(1296, [0]);
        client.Send(1296, new byte[12]);
        client.Send(1296, new byte[14]);
        client.Send(1296, nativeBody);
        WaitReply();
        Bytes(WaitState(), State(7, 2, 0, 0, 0), "畸形请求不推进且同连接继续处理合法13B请求");
        // 推进至14天并验证14天档位领取与防重。
        for (int index = 7; index < 14; index++)
            fixture.Sql("""
                INSERT INTO account_daily_attendance_claims(account,kind,reward_index,day,recipient_id,mail_id,item_id,item_count,claimed_at)
                VALUES($p0,0,$p1,20261101,$p2,999,490013043,2,1)
                """, account, index, id);
        Bytes(Request(true), State(14, 2, 2, 0, 0), "原生13B请求领取已达14次的次档累计奖励");
        That(fixture.Query("""
            SELECT c.item_id,c.item_count,i.item_uid,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value')
            FROM account_daily_attendance_claims c JOIN mailbox_attachments a ON a.mail_id=c.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE c.account=$p0 AND c.kind=1 AND c.reward_index=1
            """, account) is [[490013065L,1L,long uid14,490013065L,1L]] && uid14 > 0,
            "当前PVF累计14天档以真实UID和数量进入邮箱");
        Request(true);
        That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'attendance2370:%'") is [[2L]],
            "14天档已领取后不可重复领取");
        // 推进至21天并验证21天档位领取与防重。
        for (int index = 14; index < 21; index++)
            fixture.Sql("""
                INSERT INTO account_daily_attendance_claims(account,kind,reward_index,day,recipient_id,mail_id,item_id,item_count,claimed_at)
                VALUES($p0,0,$p1,20261101,$p2,999,490013043,2,1)
                """, account, index, id);
        Bytes(Request(true), State(21, 2, 2, 2, 0), "原生13B请求领取已达21次的三档累计奖励");
        That(fixture.Query("""
            SELECT c.item_id,c.item_count,i.item_uid,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value')
            FROM account_daily_attendance_claims c JOIN mailbox_attachments a ON a.mail_id=c.mail_id
            JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE c.account=$p0 AND c.kind=1 AND c.reward_index=2
            """, account) is [[490013066L,1L,long uid21,490013066L,1L]] && uid21 > 0,
            "当前PVF累计21天档以真实UID和数量进入邮箱");
        Request(true);
        That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'attendance2370:%'") is [[3L]],
            "21天档已领取后不可重复领取");
        clock.Advance(TimeSpan.FromDays(1));
        Bytes(Request(true), State(21, 2, 2, 2, 0), "跨日次数归零而三档累计领取位保持");
        Gm(false);
        Request(false);
        That(fixture.Query("SELECT count(*) FROM account_daily_attendance_claims") is [[24L]], "关闭保留账号21次签到与3档累计领取事实");
        Close();
        await fixture.StopAsync();
        await fixture.StartAsync();
        using var restored = Open(fixture);
        restored.Select();
        client = restored;
        Request(false);
        Gm(true);
        Bytes(Request(true), State(21, 2, 2, 2, 0), "重建Runtime及重登后1210回放完整三档已领状态");
        using (var owner = new AccountOwners().Acquire(account, () => { }))
            That(new CharacterRepository(fixture.Database).ReadDailyAttendance(owner, 20261104) is { Total: 21, Claimed: 7 },
                "重建Runtime及仓储保留同账号累计状态");
        Close();

        byte[] Request(bool enabled, byte[]? body = null)
        {
            client.Send(1296, body ?? nativeBody);
            WaitReply();
            return enabled ? WaitState() : [];
        }
        void Close()
        {
            client.Send(3, [0]);
            WaitReply(3, [1]);
            client.Closed();
        }
        void WaitReply(ushort opcode = 1296, byte[]? expected = null)
        {
            for (int count = 0; count < 150; count++)
            {
                var packet = client.Read();
                if (packet.Kind == 1 && packet.Opcode == opcode) { Bytes(packet.Body, expected ?? [], "实际命令ACK完整帧"); return; }
                Observe(packet);
            }
            throw new InvalidDataException("未收到签到应答");
        }
        byte[] WaitState()
        {
            for (int count = 0; count < 150; count++)
            {
                var packet = client.Read();
                if (packet.Kind == 0 && packet.Opcode == 1210) return packet.Body;
                Observe(packet);
            }
            throw new InvalidDataException("未收到签到状态");
        }
        void Observe(WirePacket packet)
        {
            if (packet.Kind == 0 && packet.Opcode == 1210)
                That(packet.Body.Length == 37 && U32(packet.Body, 0) == 2370, "异步状态严格37B且属于2370");
            else if (packet.Kind == 0 && packet.Opcode == 108) CheckEvents(packet.Body);
            else if (packet.Kind == 0 && packet.Opcode == 706) Bytes(packet.Body, client.MileageLimits, "活动更新保持积分商城");
            else if (packet.Kind == 0 && packet.Opcode == 99) Bytes(packet.Body, [1, 0], "邮件提醒完整99");
            else if (packet.Kind == 0 && packet.Opcode == 541)
            {
                var used = DailyBoosterProjectionTests.Decode(packet.Body);
                That(used.Count > 0 && used.Values.All(value => value == 0) && used.Keys.All(client.DailyBoosterUsed.ContainsKey),
                    "跨日保留原有每日物品名册及零使用次数");
            }
            else client.ValidateServiceDayNotice(packet);
        }
        void Gm(bool on) => That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "event " + (on ? "on" : "off") + " 2370", out string reply) &&
            reply.StartsWith("OK "), "GM签到活动开关");
    }

    static byte[] State(uint total, uint first, uint second, uint third, uint clears) => Build(writer =>
    { writer.Write(2370u); writer.Write((byte)0); foreach (uint value in new[] { total, first, second, third, clears, 3u, 4u, 5u }) writer.Write(value); });

    static void CheckEvents(byte[] body)
    {
        using var source = new MemoryStream(body);
        using var zip = new ZLibStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(zip);
        HashSet<ushort> ids = [];
        int count = reader.ReadUInt16();
        for (int index = 0; index < count; index++)
        {
            ids.Add(reader.ReadUInt16()); reader.ReadUInt32(); Text(); Text();
            if (reader.ReadByte() != 0)
            { reader.ReadByte(); reader.ReadByte(); Text(); Text(); Text(); reader.ReadUInt32(); reader.ReadUInt32(); Text(); Text(); reader.ReadByte(); }
        }
        That(new ushort[] { 2037, 2378, 2395, 2532, 266 }.All(ids.Contains), "签到开关发送完整原活动目录");
        int active = reader.ReadByte();
        for (int index = 0; index < active; index++)
        {
            ushort id = reader.ReadUInt16();
            for (int field = 0; field < 12; field++)
            {
                uint value = reader.ReadUInt32();
                if (id == 2370) That(value == (field == 0 ? 2u : 0u), "签到108第一值为推荐通关目标2，其余原字段0");
            }
        }
        That(reader.Read() == -1, "108解压后完整消费");
        void Text() { int length = reader.ReadInt32(); That(length is >= 0 and <= 32768, "活动串长度有界"); That(reader.ReadBytes(length).Length == length, "完整活动串"); }
    }
}
