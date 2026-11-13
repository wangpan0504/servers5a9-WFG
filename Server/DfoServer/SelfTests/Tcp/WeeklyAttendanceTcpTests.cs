using System.IO.Compression;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class WeeklyAttendanceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        DungeonSettlementTests.RunWeeklyAttendanceResources(resources.Archive);
        var catalog = WeeklyAttendanceCatalog.Load(resources.Archive, resources.ItemResources.Items);
        That(catalog.Weeks == 9 && catalog.RequiredClears == 4 && catalog.CatchupClears == 20 && catalog.CatchupLimit == 2 &&
            catalog.Rewards.Select(row => row.ItemId).SequenceEqual(Enumerable.Range(490014139, 9)) &&
            catalog.Bonus.Select(row => row.Threshold).SequenceEqual(new[] { 2, 5, 8 }), "真实S5A9周签到九周及2/5/8累计奖励资源");
        Boundaries(catalog);
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-03T04:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "weekly-attendance"), gm: true, resources: resources,
            clock: clock, useDefaultActivities: true, monsterMockBattle: false);
        var repository = new CharacterRepository(fixture.Database);
        var events = new ServerEventService(new ServerEventRepository(fixture.Database), clock);
        foreach (ushort id in new ushort[] { 64, 312, 2520, 10005, 2257, 228, 2370 }) events.Override(id, false);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("WeeklyGiftA", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            repository.Create(owner, candidate);
            repository.Create(owner, candidate with { Name = "WeeklyGiftB" });
        }
        await fixture.StartAsync();
        using var client = Open(fixture);
        client.ExpectedOpenEvents = [282, 2449, 414, 181, 2514, 2532, 266, 2208];
        client.ExpectedActiveEvents = [2395, 2514, 2208];
        client.Select();
        byte[]? state = null;
        bool? shown = null;
        int alarms = 0;
        var defaultSign = Exchange(1115, new byte[13]);
        Bytes(defaultSign.Single(row => row.Kind == 1 && row.Opcode == 1115).Body, [1, 1, 0, 0, 0],
            "无配置实例默认接入周签到，无通关不发奖");
        That(state is { Length: 459 } && U32(state, 4) == 0 && U32(state, 0x1c4) == 1 &&
            events.Read(2208) is { Active: true, Schedule.Revision: 0 }, "首次入城初始化周签到且不补写开关");
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "event off 2208", out var closed) && closed.StartsWith("OK "),
            "GM显式关闭默认开启的2208");
        var denied = Exchange(1115, new byte[13]);
        Bytes(denied.Single(row => row.Kind == 1 && row.Opcode == 1115).Body, [0, 1], "关闭时签到失败且连接继续");
        That(shown == false, "关闭刷新完整目录并移除周签到入口");
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "event on 2208", out var reply) && reply.StartsWith("OK "), "GM正式活动开关开启2208");
        var empty = Exchange(1115, new byte[13]);
        Bytes(empty.Single(row => row.Kind == 1 && row.Opcode == 1115).Body, [1, 1, 0, 0, 0], "无通关的合法签到不发奖");
        That(state is { Length: 459 } && U32(state, 4) == 0 && U32(state, 0x1c4) == 1 && shown == true, "开启刷新完整活动目录和第一周零进度");
        Seed(0, 4);
        var sign = Exchange(1115, new byte[13]);
        Bytes(sign.Single(row => row.Kind == 1 && row.Opcode == 1115).Body, [1, 0, 0, 0, 0], "C1115真实领取成功ACK");
        That(U32(state!, 4) == 1 && U32(state!, 0x1b4) == 0 && Count() == 1 && alarms == 1, "真实领取先ACK再1096和99，进度邮件一起落库");
        var ordered = sign.Where(row => row.Opcode is 1115 or 1096 or 99).ToArray();
        int acknowledgement = Array.FindIndex(ordered, row => row is { Kind: 1, Opcode: 1115 });
        That(acknowledgement is 0 or 1, "唯一成功ACK前至多一条未领取状态");
        // 活动worker可先发布Seed后的未领取状态；逐字节验证，不能跳过任意旧包或提前邮件。
        byte[] unclaimed = new byte[459];
        unclaimed[0x1b4] = 4;
        unclaimed[0x1c4] = 1;
        unclaimed[0x1c8] = 1;
        foreach (var packet in ordered.Take(acknowledgement))
        {
            That(packet is { Kind: 0, Opcode: 1096 }, "领奖ACK前仅允许未领取状态，没有邮件或额外应答");
            Bytes(packet.Body, unclaimed, "领奖ACK前状态完整保留零总次数、四通关余额与第一周可领资格");
        }
        That(ordered.Skip(acknowledgement).Select(row => (row.Kind, row.Opcode)).SequenceEqual(
            new (byte, ushort)[] { (1, 1115), (0, 1096), (0, 99) }), "成功领取包序完整");
        Exchange(1115, new byte[13]);
        That(Count() == 1 && alarms == 1, "重复签到不重发邮件和提醒");
        client.Send(7); client.Expect(1, 7, [1]); client.Expect(0, 2);
        client.ExpectedOpenEvents = [282, 2449, 414, 181, 2514, 2532, 266, 2208];
        client.ExpectedActiveEvents = [2395, 2514, 2208];
        client.Select(1);
        Exchange(1115, new byte[13]);
        That(U32(state!, 4) == 1 && Count() == 1, "换角仍共享账号签到事实");
        clock.Advance(TimeSpan.FromDays(14));
        Seed(2, 24);
        Exchange(1115, new byte[13]);
        Exchange(1115, new byte[13]);
        That(U32(state!, 4) == 3 && U32(state!, 0x1bc) == 1 && state![0x19c] == 1 && state[0x19d] == 2 && state[0x19e] == 3,
            "第三周先普通签到再补第二周，状态完整保留已签周列表");
        byte[] bonus = Build(writer => { writer.Write(2208u); writer.Write(0u); writer.Write(0u); writer.Write(0u); });
        var cumulative = Exchange(689, bonus);
        That(cumulative.All(row => row.Kind == 0) && U32(state!, 8) == 4 && Count() == 4, "C689领取累计2次奖励，没有伪造689应答");
        Exchange(689, bonus);
        That(Count() == 4, "重复累计领取不重发");
        var itemIds = fixture.Query("""
            SELECT json_extract(i.item_core,'$.ItemId') FROM mailbox_messages m
            JOIN mailbox_attachments a ON a.mail_id=m.mail_id JOIN character_inventory_items i ON i.item_uid=a.item_uid
            WHERE m.delivery_key LIKE 'weekly2208:%' ORDER BY m.mail_id
            """).Select(row => Convert.ToInt32(row[0]));
        That(itemIds.SequenceEqual(new[] { 490014139, 490014140, 490014141, 490014148 }), "实际附件UID分别指向本版前三周及累计2次奖励");
        client.Send(3, [0]); client.Expect(1, 3, [1]); client.Closed();
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "周签到真实TCP后数据库完整");

        long Count() => Convert.ToInt64(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'weekly2208:%'").Single()[0]);
        void Seed(int week, int clears)
        {
            long start = Convert.ToInt64(fixture.Query("SELECT starts_at FROM weekly_attendance_season WHERE id=1").Single()[0]);
            var raw = fixture.Query("SELECT state FROM account_weekly_attendance WHERE account=$p0 AND season_start=$p1", fixture.Options.DevelopmentAccount, start);
            var ledger = WeeklyAttendanceState.Parse(raw.Length == 0 ? null : (string)raw[0][0], catalog);
            ledger.Clears[week] = clears;
            fixture.Sql("INSERT INTO account_weekly_attendance VALUES($p0,$p1,$p2) ON CONFLICT(account,season_start) DO UPDATE SET state=excluded.state",
                fixture.Options.DevelopmentAccount, start, ledger.Serialize());
        }
        WirePacket[] Exchange(ushort opcode, byte[] body)
        {
            client.Send(opcode, body);
            client.SendBarrier();
            var rows = new List<WirePacket>();
            for (int i = 0; i < 200; i++)
            {
                var packet = client.Read();
                if (packet is { Kind: 1, Opcode: 1347 })
                {
                    Bytes(packet.Body, [1], "周签到后续命令屏障");
                    return rows.ToArray();
                }
                rows.Add(packet);
                if (packet is { Kind: 0, Opcode: 1096 }) state = packet.Body;
                else if (packet is { Kind: 0, Opcode: 541 })
                {
                    var used = DailyBoosterProjectionTests.Decode(packet.Body);
                    That(used.Count > 0 && used.Values.All(value => value == 0) && used.Keys.All(client.DailyBoosterUsed.ContainsKey),
                        "跨周仍保留原每日礼盒完整名册，测试角色没有使用次数");
                }
                else if (packet is { Kind: 0, Opcode: 99 }) { Bytes(packet.Body, [1, 0], "新邮件提醒"); alarms++; }
                else if (packet is { Kind: 0, Opcode: 108 })
                {
                    using var input = new MemoryStream(packet.Body);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    zlib.CopyTo(output);
                    byte[] data = output.ToArray();
                    shown = Enumerable.Range(0, U16(data)).Any(i => U16(data, 2 + 15 * i) == 2208);
                }
                else if (packet.Kind == 0 && packet.Opcode != 706) client.ValidateServiceDayNotice(packet);
                else That(packet.Kind == 1 && packet.Opcode == 1115 || packet.Opcode == 706, "没有额外未知ACK");
            }
            throw new InvalidOperationException("周签到响应超过包预算");
        }
    }

    static void Boundaries(WeeklyAttendanceCatalog catalog)
    {
        var at = DateTimeOffset.Parse("2026-11-03T04:00:00Z");
        var season = WeeklyAttendanceSeason.Create(at, catalog.Weeks);
        var state = WeeklyAttendanceState.Parse(null, catalog);
        var fourth = DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).AddDays(21);
        state.Clears[3] = catalog.MaximumClears;
        for (int i = 0; i < 3; i++) state.Claim(catalog, season, fourth, true, false);
        state.Clears[3] = 20;
        That(state.Catchups == 2 && state.Signed == 11 && state.Claim(catalog, season, fourth, true, false).IsEmpty &&
            state.Clears[3] == 20, "全季两次补签用完后，仍有余额与缺周也不能第三次补签");
        for (int week = 4; week < 9; week++)
        {
            state.Clears[week] = 4;
            state.Claim(catalog, season, DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).AddDays(7 * week), true, false);
        }
        var ninth = DateTimeOffset.FromUnixTimeSeconds(season.StartsAt).AddDays(56);
        That(state.Claim(catalog, season, ninth, false, true).Select(row => row.Threshold).SequenceEqual(new[] { 2, 5, 8 }) &&
            state.Bonus == ((1u << 2) | (1u << 5) | (1u << 8)), "一次累计请求完整领取2/5/8三档，各位独立");
        That(state.Claim(catalog, season, DateTimeOffset.FromUnixTimeSeconds(season.EndsAt), true, true).IsEmpty,
            "赛季结束不再普通签到、补签或累计发奖");
    }
}
