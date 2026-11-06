using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 验证本服显式周期策略下在线任务快照刷新。
internal static class BattlePassOnlinePeriodTcpTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
    const string Account = "10000";

    internal static async Task ManualAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunAsync(source, root, resources, false, false);
        await RunAsync(source, root, resources, true, false);
    }

    internal static Task DailyAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunAsync(source, root, resources, false, true);

    internal static Task WeeklyAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunAsync(source, root, resources, true, true);

    internal static Task UtcAsync(ServerOptions source, string root, TcpTestResources resources) =>
        RunAsync(source, root, resources, false, true, utc: true);

    static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources, bool weekly, bool automatic, bool utc = false)
    {
        string name = (weekly ? "weekly" : "daily") + (automatic ? "-automatic" : "-manual") + (utc ? "-utc" : "");
        var boundary = Start.AddDays(weekly ? 7 : 1);
        var clock = new DailyBoosterTestClock(boundary.AddSeconds(-1), utc ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        var catalog = BattlePassCatalog.Load(resources.Archive);
        var policy = new BattlePassMissionPolicy("online-period-" + name, Start, catalog);
        var options = source with { BattlePassSeasonKey = policy.SeasonKey, BattlePassSeasonStart = Start };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "battle-pass-period-" + name),
            resources: resources, clock: clock, sharedResourcePath: true);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(Account, () => { }))
            repository.Create(owner, new NewCharacter("PassPeriod", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 0, GrowType = 33, Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills });
        await fixture.StartAsync();
        using var peer = Open(fixture, Account);
        peer.ExpectedOpenEvents = [282, 2449, 414, 181, 2532, 266, 2520];
        peer.ExpectedBattlePassEnd = checked((uint)policy.End.ToUnixTimeSeconds());

        byte[] ReadAuthoritative()
        {
            var observer = new CharacterRepository(fixture.Database);
            observer.ConfigureBattlePassMissions(policy);
            using var owner = new AccountOwners().Acquire(Account, () => { });
            return observer.ReadBattlePassMissions(owner, clock.GetUtcNow())!.ToPayload();
        }
        void SelectAndCheck()
        {
            peer.Select();
            peer.Expect(0, 1800);
            byte[] ready = peer.Expect(0, 1801).Body;
            peer.Expect(0, 1802);
            peer.Barrier();
            Bytes(ready, ReadAuthoritative(), "Ready returns all 156 authoritative mission bytes after login");
        }

        // 首次选角初始化赛季，再与同账号的独立仓储读取比较。
        peer.Select();
        peer.Expect(0, 1800);
        byte[] before = peer.Expect(0, 1801).Body;
        peer.Expect(0, 1802);
        peer.Barrier();
        Bytes(before, ReadAuthoritative(), "initial full mission snapshot");
        ushort id = peer.Id;
        await clock.WaitForOneShotTimerAsync(boundary);
        clock.Advance(TimeSpan.FromSeconds(1));
        byte[] expected = ReadAuthoritative();
        That(!before.AsSpan(0, 24).SequenceEqual(expected.AsSpan(0, 24)), "daily four slots actually change");
        That(weekly != before.AsSpan(24, 24).SequenceEqual(expected.AsSpan(24, 24)), "weekly slots change only at weekly boundary");
        Bytes(before.AsSpan(48, 108).ToArray(), expected.AsSpan(48, 108).ToArray(), "season slots stay unchanged");

        // 等待已有日切礼盒分片和日切工作器在命令门禁内发送的通知。
        // UTC时区使用旧服务日的冒险团头顶通知，不把本地强袭日界误作战令日界。
        int chunks = 0, workerNotice = 0, snapshots = 0;
        byte[][] daily = peer.InitialDailyBoosterPackets;
        That(daily.Length > 0, "initial daily booster chunks exist");
        void Accept(WirePacket packet)
        {
            if (packet is { Kind: 0, Opcode: 541 })
            {
                That(chunks < daily.Length, "no extra daily booster chunks");
                Bytes(packet.Body, daily[chunks++], "complete daily booster chunk");
            }
            else if (packet is { Kind: 0, Opcode: 1801 })
            {
                That(++snapshots == 1, "no duplicate mission snapshot");
                Bytes(packet.Body, expected, "automatic complete authoritative mission snapshot");
            }
            else
            {
                if (packet.Kind == 0 && packet.Opcode == (utc ? 1349 : 1536))
                    That(++workerNotice == 1, "one service-day worker notice");
                peer.ValidateServiceDayNotice(packet);
            }
        }
        int budget = 0;
        while (chunks < daily.Length || workerNotice == 0)
        {
            That(++budget <= 256, "daily notifications bounded");
            Accept(peer.Read());
        }
        peer.SendBarrier();
        while (true)
        {
            That(++budget <= 256, "barrier notifications bounded");
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body, [1], "barrier ACK"); break; }
            Accept(packet);
        }
        That(peer.Id == id, "same connection and character across boundary");
        if (automatic)
            That(snapshots == 1, "online period boundary publishes one complete 1801 without a request");
        else
        {
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            SelectAndCheck();
            That(peer.Id == id, "same connection reselects the original character");
        }
        await fixture.StopAsync();
    }
}
