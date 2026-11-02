using System.Collections.Concurrent;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassGuildPostOrderingRace
{
    internal static Task RunCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, nextDay: false);
    internal static Task RunNextDayCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, nextDay: true);

    static async Task RunScenarioAsync(TcpTestResources resources, bool nextDay)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-guild-post-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "pass-guild-post", season = "guild-post-order-race";
            var catalog = BattlePassCatalog.Load(resources.Archive);
            var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
            var policy = new BattlePassMissionPolicy(season, start, catalog);
            var day = Enumerable.Range(0, policy.Days - (nextDay ? 1 : 0)).Select(index => start.AddDays(index).AddHours(2))
                .First(at => policy.Active(at).Any(row => row.Key.Category == 0 && row.Key.Id == 9));
            var rules = new GuildHallWaitCatalog(resources.Archive);
            That(rules is { Points: 3, IntervalSeconds: 300 } &&
                catalog.MissionsById.TryGetValue(9, out var definition) && BattlePassGuildContributionSource.Matches(definition),
                "匹配资源提供真实大厅等待周期和公会贡献任务定义");
            var clock = new RaceClock(day);
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            var hall = new Location(70, 0, 100, 100);
            var outside = new Location(1, 0, 100, 100);
            var created = repository.Create(owner, new NewCharacter("PassPost", hall, outside) { Level = 95 });
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'Wait guild','wait',{created.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{created.Id},1,1000)
                """);
            var bounds = new[] { new WalkRegion(new(0, 0, 1000, 1000), [], []) };
            TownArea Area(Location place, string map, Location target) => new(place.Town, place.Area, 0, [], [], [], null,
                new(map, bounds, [new(new(0, 0, 1000, 1000), target.Town, target.Area, [], [])], false, []), [], []);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>
            {
                [(hall.Town, hall.Area)] = Area(hall, "map/guildagit_2019/guildagit_inside.map", outside),
                [(outside.Town, outside.Area)] = Area(outside, "map/village/normal.map", hall)
            }, outside, outside, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(created.Slot, resumeTown: true); session.MarkReady();
            var selected = session.Selected!;
            long epoch = session.SelectionEpoch;
            That(selected.Guild is { Id: 1 } && towns.IsGuildHall(selected.Position, new HashSet<int>()),
                "隔离角色在真实大厅地图且持有真实会籍");
            var online = new OnlineSessionDirectory(); online.Register(owner, session);
            using var invitations = new GuildInvitations();
            var management = new GuildManagementService(repository, online, invitations, new GuildPermitRegistry(), clock: clock);
            var items = resources.ItemResources;
            var contribution = new GuildContributionService(repository, items.Items, items.GoldLimits, items.EpicPieces, management, clock);
            var projection = new BattlePassProjection(season, catalog, clock);
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var sent = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Enqueue((kind, opcode, body.ToArray(), action)));
            var firstCaptured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            online.RegisterConnection(1, session, (_, _, _) => { }, guardedProbe: (opcode, body, action, guard) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstCaptured.TrySetResult(body.ToArray());
                    releaseFirst.Task.GetAwaiter().GetResult();
                }
                return sender.TryPost(0, opcode, body, action, guard);
            });
            var sweep = new GuildHallWaitService(repository, rules, online, contribution, clock,
                contributionCommitted: (actor, id, at) => BattlePassMissionProjection.Post(actor, id, online, at));
            _ = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
            sweep.Sweep();
            That(Number("SELECT count(*) FROM guild_contribution_events WHERE source=7") == 0,
                "首次大厅观察只开始计时");
            clock.Advance(rules.IntervalSeconds);
            Task firstSweep = Task.Run(sweep.Sweep);
            try
            {
                byte[] firstBody = await firstCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var first = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
                That(first.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 9).Count == 3 &&
                    firstBody.SequenceEqual(first.ToPayload()) && Number("SELECT count(*) FROM guild_contribution_events WHERE source=7") == 1 &&
                    ReferenceEquals(selected, session.Selected) && session.SelectionEpoch == epoch,
                    "首次真实贡献已提交，原3点1801已捕获但尚未入队");
                clock.Advance(nextDay ? 86400 : rules.IntervalSeconds);
                if (!nextDay) sweep.Sweep();
                var latest = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
                That((nextDay ? calls == 1 && latest.Active && latest.Season.Revision == first.Season.Revision &&
                    !latest.ToPayload().SequenceEqual(first.ToPayload()) &&
                    Number("SELECT count(*) FROM guild_contribution_events WHERE source=7 AND points=3") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='guild_contribution'") == 1 :
                    calls == 2 && latest.Season.Revision == first.Season.Revision + 1 &&
                    latest.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 9).Count == 6 &&
                    Number("SELECT count(*) FROM guild_contribution_events WHERE source=7 AND points=3") == 2 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='guild_contribution'") == 2) &&
                    ReferenceEquals(selected, session.Selected) && session.SelectionEpoch == epoch,
                    "真实贡献或跨日后形成新的权威任务状态，原角色快照和纪元不变");
                var current = session.ReadBattlePassMissionState(clock.GetUtcNow())!;
                var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
                await projection.MissionStateAsync(context, current.State, current.Receipt).WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var beforeRelease = sent.Where(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION).ToArray();
                That(beforeRelease.Length >= 1 && beforeRelease[^1].Body.SequenceEqual(latest.ToPayload()),
                    "释放旧通知前，当前完整三页投影已写出最新1801");
                releaseFirst.TrySetResult();
                await firstSweep.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var frames = sent.Where(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION).ToArray();
                That(frames.Length >= 1 && frames[^1].Body.SequenceEqual(latest.ToPayload()),
                    "旧Post延迟入队后最终1801仍保持当前任务状态");
            }
            finally
            {
                releaseFirst.TrySetResult();
                await firstSweep.WaitAsync(TimeSpan.FromSeconds(5));
            }

            void Sql(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            long Number(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    sealed class RaceClock(DateTimeOffset origin) : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => origin.AddTicks(Interlocked.Read(ref ticks));
        public void Advance(int seconds) => Interlocked.Add(ref ticks, seconds * TimeSpan.TicksPerSecond);
    }
}
