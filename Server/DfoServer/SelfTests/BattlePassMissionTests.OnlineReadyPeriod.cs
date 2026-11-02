using System.Collections.Concurrent;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    internal static async Task RunOnlineReadyPeriodAsync()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-ready-period-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            foreach (bool starting in new[] { false, true }) await Scenario(starting);
            succeeded = true;
            Console.WriteLine($"PASS BattlePassMissionTests online Ready period ({Count - checks} checks)");
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"战令首刷新跨期回归失败，隔离现场保留于：{root}");
        }

        async Task Scenario(bool starting)
        {
            string name = starting ? "start" : "daily";
            string season = "ready-period-" + name;
            var (policy, _) = AckFixturePolicy(season);
            var boundary = starting ? policy.Start : policy.Start.AddDays(59);
            var clock = new OnlineReadyPeriodClock(boundary.AddSeconds(-1));
            string path = Path.Combine(root, name + ".db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("ready-period-" + name, () => { });
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point,
                new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            repository.Create(owner, new NewCharacter("ReadyPeriod", point, null) { Level = 95 });
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(0);
            session.MarkReady();
            var before = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;

            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var sent = new ConcurrentQueue<byte[]>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (_, opcode, _, body, _) =>
                {
                    if (opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION) sent.Enqueue(body.ToArray());
                });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            var battlePass = new BattlePassProjection(season, policy.Catalog, clock);
            var town = new TownProjection(towns, null!, null, null!, clock: clock) { BattlePassMissions = policy };
            using var log = new ServerLog(root);
            await using var serviceDay = new ServiceDayProjection(context, log, town, clock);

            await battlePass.ReadyAsync(context).WaitAsync(TimeSpan.FromSeconds(5));
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(starting ? !before.Active && sent.IsEmpty : before.Active && sent.Count == 1 &&
                sent.Single().SequenceEqual(before.ToPayload()),
                "Ready已实际完成旧期任务页，开季前则没有虚构1801：" + name);
            int prior = sent.Count;
            long sourceCount = SourceCount();

            // Ready最后一次权威重读已经完成，但ServiceDay首次刷新尚未发生。
            clock.Set(boundary);
            var expected = repository.ReadBattlePassMissions(owner, boundary)!;
            That(expected.Active && expected.ToPayload().Length == 156 &&
                (starting || !expected.ToPayload().SequenceEqual(before.ToPayload())),
                "边界产生完整且可辨识的新期任务页：" + name);
            await context.CommandGate.WaitAsync();
            try
            {
                await serviceDay.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                That(sent.Count == prior + 1 && sent.Last().SequenceEqual(expected.ToPayload()),
                    "第一次ServiceDay刷新补发恰一份当前156B任务页：" + name);
                await serviceDay.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                That(sent.Count == prior + 1, "同一期段再次刷新不重复补发1801：" + name);
            }
            finally { context.CommandGate.Release(); }

            var restored = repository.ReadBattlePassMissions(owner, boundary)!;
            That(restored.Season.Revision == before.Season.Revision &&
                restored.Season.TotalExperience == before.Season.TotalExperience &&
                restored.Season.PassType == before.Season.PassType && restored.Season.Claims.SetEquals(before.Season.Claims) &&
                restored.ToPayload().SequenceEqual(expected.ToPayload()) &&
                SourceCount() == sourceCount, "跨期投影不增加登录来源、经验或领取记录：" + name);

            long SourceCount()
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT count(*) FROM account_battle_pass_source_events";
                return (long)command.ExecuteScalar()!;
            }
        }
    }

    sealed class OnlineReadyPeriodClock(DateTimeOffset now) : TimeProvider
    {
        long ticks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("BP-ready-period", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref ticks, value.UtcTicks);
    }
}
