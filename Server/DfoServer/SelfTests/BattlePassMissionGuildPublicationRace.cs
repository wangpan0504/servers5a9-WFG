using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassMissionGuildPublicationRace
{
    internal static Task RunCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, "mission");
    internal static Task RunReadyCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, "ready");
    internal static Task RunCertificateCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, "certificate");
    internal static Task RunRewardCandidateAsync(TcpTestResources resources) => RunScenarioAsync(resources, "reward");

    static async Task RunScenarioAsync(TcpTestResources resources, string scenario)
    {
        bool ready = scenario == "ready", mission = scenario == "mission", reward = scenario == "reward", certificate = scenario == "certificate";
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-guild-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "pass-guild-publish", season = "guild-publication-race";
            var catalog = BattlePassCatalog.Load(resources.Archive);
            var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
            var policy = new BattlePassMissionPolicy(season, start, catalog);
            That(catalog.MissionsById.TryGetValue(9, out var definition) && BattlePassGuildContributionSource.Matches(definition),
                "匹配资源提供真实公会贡献日任务9及完整150目标");
            var day = Enumerable.Range(0, policy.Days).Select(index => start.AddDays(index).AddHours(2))
                .First(at => policy.Active(at).Any(row => row.Key.Category == 0 && row.Key.Id == 9) &&
                    policy.Active(at).Any(row => row.Key.Category == 0 && row.Key.Id != 9 && !row.Premium));
            var clock = new RaceClock(day);
            var rules = new GuildHallWaitCatalog(resources.Archive);
            That(rules is { Points: 3, IntervalSeconds: 300 } && day.AddSeconds(rules.IntervalSeconds) < start.AddDays(policy.Days),
                "大厅积分和等待周期读取匹配资源；测试时钟处于同一活动日");
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            var hall = new Location(70, 0, 100, 100);
            var outside = new Location(1, 0, 100, 100);
            var created = repository.Create(owner, new NewCharacter("PassGuild", hall, outside) { Level = 95 });
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
            if (certificate)
                That(DfoServer.Development.DevelopmentGm.Answer("recharge cera 10000", owner.Account, repository, owners, _ => session)
                    .StartsWith("OK ", StringComparison.Ordinal), "资格证场景使用真实在线充值准备点券");
            var selected = session.Selected!;
            long epoch = session.SelectionEpoch;
            That(selected.Guild is { Id: 1 } && towns.IsGuildHall(selected.Position, new HashSet<int>()),
                "隔离角色在实际大厅地图且持有真实持久会籍");
            var online = new OnlineSessionDirectory(); online.Register(owner, session);
            using var invitations = new GuildInvitations();
            var management = new GuildManagementService(repository, online, invitations, new GuildPermitRegistry(), clock: clock);
            var itemResources = resources.ItemResources;
            var contribution = new GuildContributionService(repository, itemResources.Items, itemResources.GoldLimits,
                itemResources.EpicPieces, management, clock);
            using var log = new ServerLog(Path.Combine(root, "log"));
            var projection = new BattlePassProjection(season, catalog, clock);
            var stats = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(stats, new TownProjection(towns, stats, null, itemResources.EpicPieces), itemResources.EpicPieces);
            var commands = new GameCommandRegistry();
            new BattlePassMissionHandler(clock, projection.MissionStateAsync, log).Register(commands);
            var cash = new InventoryCashShopService(itemResources.Items, itemResources.CashShop, itemResources.GoldLimits,
                itemResources.EpicPieces, itemResources.Cargo, contracts: itemResources.Contracts, avatars: itemResources.AvatarPackages, clock: clock);
            new ShopHandler(new(itemResources.Items, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces),
                cash, inventory, log, battlePassCertificateChanged: projection.MissionStateAsync).Register(commands);
            var rewardService = new BattlePassRewardService(catalog, itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, clock);
            new BattlePassHandler(rewardService, season, inventory, log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new HoldFrame(reward ? NotiPacketType.BATTLE_PASS_INFO_REWARD : NotiPacketType.BATTLE_PASS_INFO_PASS);
            var sent = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Enqueue((kind, opcode, body.ToArray(), action)));
            int posted = 0;
            online.RegisterConnection(1, session, (_, _, _) => { }, guardedProbe: (opcode, body, action, guard) =>
            {
                bool accepted = sender.TryPost(0, opcode, body, action, guard);
                if (accepted) Interlocked.Increment(ref posted);
                return accepted;
            });
            var sweep = new GuildHallWaitService(repository, rules, online, contribution, clock,
                contributionCommitted: (actor, id, at) => BattlePassMissionProjection.Post(actor, id, online, at));
            sweep.Sweep();
            That(Number("SELECT count(*) FROM guild_contribution_events WHERE source=7") == 0,
                "首次自然大厅观察只建立计时，不提前发分");
            clock.Advance(rules.IntervalSeconds);
            _ = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
            var claim = policy.Active(clock.GetUtcNow()).First(row => row.Key.Category == 0 && row.Key.Id != 9 && !row.Premium);
            // 仅在隔离数据库种入任务达成或等级资格；业务提交和大厅贡献使用真实事务。
            if (mission)
                Sql("INSERT INTO account_battle_pass_mission_progress VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                    account, season, claim.Key.Category, claim.Key.Period, claim.Key.Id, claim.Definition.RequiredCount);
            int rewardLevel = 0;
            if (reward)
            {
                rewardLevel = 4;
                That(catalog.RewardsByLevel[4].Rewards.Where(item => item.RawTrackCode == 0)
                    .Select(item => (item.ItemId, item.Count)).SequenceEqual(new[] { (15, 1) }),
                    "四级普通奖励使用已验证的匹配资源定义");
                Sql("UPDATE account_battle_pass_seasons SET total_experience=$p0 WHERE account=$p1 AND season_key=$p2",
                    checked((long)catalog.TotalExperienceToReach(rewardLevel)), account, season);
            }
            byte[] request = new byte[24]; request[13] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(14), checked((ushort)claim.Key.Id));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), checked((uint)claim.ViewSlot));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            byte[] buy = new byte[19]; buy[2] = 1; buy[5] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(buy.AsSpan(7), 105659);
            byte[] rewardBody = new byte[19]; BinaryPrimitives.WriteUInt32LittleEndian(rewardBody.AsSpan(15), checked((uint)rewardLevel));
            Task dispatch = scenario switch
            {
                "ready" => projection.ReadyAsync(context),
                "mission" => commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BATTLE_PASS_REQUEST_MISSION, request)),
                "certificate" => commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, buy)),
                _ => commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BATTLE_PASS_REQUEST_REWARD, rewardBody))
            };
            try
            {
                await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var oldState = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
                var first = sent.ToArray();
                var committedCharacter = session.Selected!;
                That(output.HeldKind == 0 && output.HeldOpcode == (ushort)(reward ? NotiPacketType.BATTLE_PASS_INFO_REWARD : NotiPacketType.BATTLE_PASS_INFO_PASS) &&
                    (ready ? first.Length == 0 : mission ? first.Length == 1 && first[0].Kind == 1 && first[0].Opcode == (ushort)CmdPacketType.BATTLE_PASS_REQUEST_MISSION &&
                    first[0].Body.SequenceEqual(new byte[] { 1 }.Concat(request.Skip(13))) &&
                    oldState.Season.TotalExperience == claim.Experience &&
                    oldState.Slots.Single(row => row.Instance.Key == claim.Key).Completed &&
                    Number("SELECT count(*) FROM account_battle_pass_mission_claims") == 1 :
                    certificate ? first.Count(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM && row.Body[0] == 1) == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_certificate_orders") == 1 && oldState.Season.PassType == 1 &&
                    committedCharacter.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 5100 :
                    first.Count(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BATTLE_PASS_REQUEST_REWARD && row.Body.SequenceEqual(new byte[] { 1 })) == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_claims") == 1 &&
                    committedCharacter.Inventory.Items.Count(item => item.Core.ItemId == 15 && item.Core.Count == 1) == 1) &&
                    oldState.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 9).Count == 0 &&
                    (mission || ready ? ReferenceEquals(selected, committedCharacter) : committedCharacter.Id == selected.Id),
                    "原业务已提交并发送唯一ACK，战令帧正在写入");
                sweep.Sweep();
                var latest = repository.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
                That(posted == 1 && Number("SELECT count(*) FROM guild_contribution_events WHERE source=7 AND points=3") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='guild_contribution'") == 1 &&
                    latest.Season.Revision == oldState.Season.Revision + 1 && latest.Season.TotalExperience == oldState.Season.TotalExperience &&
                    latest.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 9).Count == 3 &&
                    ReferenceEquals(committedCharacter, session.Selected) && session.SelectionEpoch == epoch,
                    "真实大厅计时事务增加3点及季版本，原Selected和选角纪元不变，新1801已入原sender");
                output.Release.TrySetResult();
                await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var frames = sent.ToArray();
                var missions = frames.Where(frame => frame.Kind == 0 && frame.Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION).ToArray();
                That(frames.Count(frame => frame.Kind == 1 && frame.Opcode == (ushort)CmdPacketType.BATTLE_PASS_REQUEST_MISSION) == (mission ? 1 : 0) &&
                    Number("SELECT count(*) FROM account_battle_pass_mission_claims") == (mission ? 1 : 0) &&
                    Number("SELECT count(*) FROM account_battle_pass_certificate_orders") == (certificate ? 1 : 0) &&
                    Number("SELECT count(*) FROM account_battle_pass_claims") == (reward ? 1 : 0),
                    "交错发布不重复业务事务");
                That(missions.Length >= 1 && missions.Any(frame => frame.Body.SequenceEqual(latest.ToPayload())) &&
                    missions[^1].Body.SequenceEqual(latest.ToPayload()),
                    "所有排队通知结束后，客户端最后的1801为真实大厅贡献后的最新任务状态");
                if (certificate)
                {
                    var pass = frames.Where(frame => frame.Kind == 0 && frame.Opcode is
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS or (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION or
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
                    That(frames.Count(frame => frame.Kind == 1 && frame.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM) == 1 &&
                        Number("SELECT count(*) FROM currency_audit_log WHERE action_name='cash_shop_buy' AND currency_kind=15") == 1 &&
                        repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 5100 &&
                        pass.Length >= 3 && pass.TakeLast(3).Select(row => row.Opcode).SequenceEqual(new ushort[] {
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION,
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD }) &&
                        pass[^3].Body.SequenceEqual(BattlePassProjection.PassInfo(catalog, latest.Season)) &&
                        pass[^2].Body.SequenceEqual(latest.ToPayload()) && pass[^1].Body.SequenceEqual(latest.Season.ToClaimBytes()),
                        "资格证购买最终完整三页均来自最新赛季状态");
                }
                if (reward)
                    That(frames.Count(frame => frame.Kind == 1 && frame.Opcode == (ushort)CmdPacketType.BATTLE_PASS_REQUEST_REWARD) == 1 &&
                        repository.LoadCharacter(owner, 0).Inventory.Items.Count(item => item.Core.ItemId == 15 && item.Core.Count == 1) == 1 &&
                        missions.Length == 1 && frames.Any(frame => frame.Kind == 0 &&
                        frame.Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD &&
                        frame.Body.SequenceEqual(latest.Season.ToClaimBytes())),
                        "奖励只发布领取位，不覆盖大厅发布的最新1801");
            }
            finally { output.Release.TrySetResult(); }

            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
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
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => origin.AddTicks(ticks);
        public void Advance(int seconds) => ticks += seconds * TimeSpan.TicksPerSecond;
    }

    sealed class HoldFrame(NotiPacketType target) : MemoryStream
    {
        int writes;
        public byte HeldKind { get; private set; }
        public ushort HeldOpcode { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 16 && buffer.Span[0] == 0 &&
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == (ushort)target && Interlocked.Exchange(ref writes, 1) == 0)
            {
                HeldKind = buffer.Span[0];
                HeldOpcode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]);
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
