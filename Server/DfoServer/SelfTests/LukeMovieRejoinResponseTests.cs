using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeMovieRejoinResponseTests
{
    // 正式Start/资格SQL及重返身份；清图事实和10400显示规格明确为合成输入，不冒充真实死亡或原服奖励。
    internal static async Task RunAsync(TcpTestResources resources, RaidDefinition definition, LukeRaidStartPolicy policy)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-luke-movie-rejoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "game.db");
        var sessions = new List<GameSession>();
        var leases = new List<AccountOwners.Lease>();
        bool passed = false;
        try
        {
            var items = resources.ItemResources;
            var repository = new CharacterRepository(database, resources.Quests);
            var owners = new AccountOwners();
            var raids = new RaidRegistry();
            var parties = new PartyService(repository);
            var clock = new LukeRaidClockTests.Clock(DateTimeOffset.UtcNow);
            var candidates = new AntonRaidRejoin(true, clock: clock);
            raids.BindRejoin(candidates);
            var recovery = new LukeRaidRejoin(raids, parties, candidates);
            policy = policy with { Completion = policy.Completion! with
            {
                Phases = policy.Completion.Phases.Select(phase => phase with { Rewards = phase.Rewards with
                {
                    Offers = phase.Rewards.Offers.Select(offer => offer with { LukeResult = new(1, 10400, 1, 0, 0, 0) }).ToImmutableArray()
                } }).ToImmutableArray()
            } };
            That(policy.Completion.Phases.All(phase => phase.Rewards.Offers.Length == 2 &&
                phase.Rewards.Offers.All(offer => offer.LukeResult!.Matches(offer))),
                "合成显示规格须匹配真实10400单件奖励，原两kind不加重复Card");
            var start = new LukeRaidStart(raids, parties, repository, resources.Towns, items.EpicPieces, policy,
                clock, rewardItems: items.Items);
            RaidActor Login(int index, bool create)
            {
                var owner = owners.Acquire("movie-rejoin-" + index, () => { });
                leases.Add(owner);
                if (create)
                {
                    var origin = new Location(38, 0, 629, 196);
                    var candidate = CharacterCreation.Prepare("LkMovie" + index, resources.Characters.Get(0), origin, origin, resources.Learning.Get);
                    var learning = SkillPointLedger.Synchronize(0, 0, 90,
                        candidate.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(90) }, resources.Learning);
                    var role = repository.Create(owner, candidate with
                    { Level = 90, AcceptedStoryLevel = 90, CompletedQuests = [2903, 4872], InitialLearning = learning });
                    var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
                    InventoryService.Commit(repository, owner, role,
                        inventory.Grant(role, [new(definition.Resources!.Script.EntryCost!.ItemId, 2)]));
                }
                var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
                    entry: new(new(2, 20), 32, "[raid]"));
                sessions.Add(session);
                session.Select(0); session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
                return session.CaptureRaidActor(raids)!;
            }
            var actors = Enumerable.Range(0, 8).Select(index => Login(index, true)).ToArray();
            That(raids.Create(actors[0], 1, "Movie", "Movie"u8.ToArray(), 8, 1) is not null, "原Registry创建卢克团");
            foreach (var actor in actors.Skip(1))
            {
                raids.Invite(actors[0], actor, 0);
                That(raids.Respond(actor, actors[0].Character, true).Accepted, "原邀请加入电影重返夹具");
            }
            for (int index = 0; index < actors.Length; index++) raids.Assign(actors[0], actors[index].Character, (byte)(index / 4 + 1));
            That(start.Start(actors[0], raids.Find(actors[0])!, definition, [], () => true).Error == 0,
                "原Start消费真实门票并安装Raid/Party/Session");
            var battle = actors[0].Session.CurrentRaidBattle(actors[0])!;
            void CompletePhase(int phase, uint dungeon)
            {
                int count = policy.ExecutionScript.Phases[phase].Dungeons.Single(row => row.Id == dungeon).HpRaw;
                for (int turn = 0; turn < count; turn++)
                {
                    var run = Guid.NewGuid();
                    var rows = actors.Take(4).Select(actor => (Actor: actor,
                        Identity: new DungeonRunIdentity(actor.Character, 0, 1, run, 1))).ToArray();
                    using var reservation = battle.ReserveDungeon(new(battle.Instance, phase, 1, RaidKind.Luke), dungeon, rows);
                    That(reservation is not null, "合成清图前沿Battle原Run预约准入");
                    reservation!.Install();
                    var facts = rows.Select(row => (row.Actor, new DungeonClearFact(row.Identity, dungeon, [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/movie-rejoin.map") })).ToArray();
                    That(battle.CompleteLukeDungeon(facts) is not null, "合成ClearFact经过原阶段规则");
                    if (turn == count - 1)
                        That(actors[0].Session.CommitPendingLukePhase(repository), "原生命周期writer密封本阶段资格SQL");
                    foreach (var row in rows) battle.ReleaseDungeon(row.Actor, run);
                }
                That(battle.View is { State: AntonRaidBattleState.Reward } && battle.View.Phase == phase &&
                    actors.All(actor => battle.LukeEligibility(actor.Character, phase) is { Eligible: true }),
                    "全部Run退出后才开放真实Reward，最小参与数0的八人资格已提交");
            }
            CompletePhase(0, 5054);
            foreach (var actor in actors) That(battle.CompleteLukeFirstMovie(actor, battle.View), "真实P1完成入口逐人确认");
            foreach (var actor in actors)
                foreach (byte kind in new byte[] { 1, 0 })
                {
                    var receipt = actor.Session.ClaimRaidReward(kind, 1, repository, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                    That(receipt is not null, "P1沿原资产writer领取原两kind");
                    actor.Session.ConfirmRaidRewardPublished(receipt!);
                }
            That(battle.View is { Phase: 1, State: AntonRaidBattleState.Fighting }, "传入无等待策略沿原P1领奖推进P2");
            CompletePhase(1, 5311);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var held = new HeldWriteStream();
            using var peerStream = new MemoryStream();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            var peerFrames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            object gate = new();
            await using var sender = new GameConnectionSender(actors[0].Owner, held, stop, 0,
                (kind, opcode, _, body, _) => { lock (gate) frames.Add((kind, opcode, body.ToArray())); });
            await using var peerSender = new GameConnectionSender(actors[1].Owner, peerStream, stop, 0,
                (kind, opcode, _, body, _) => { lock (gate) peerFrames.Add((kind, opcode, body.ToArray())); });
            var context = new GameConnectionContext(new(91, IPAddress.Loopback, held, stop), actors[0].Owner, actors[0].Session, sender);
            var peerContext = new GameConnectionContext(new(92, IPAddress.Loopback, peerStream, stop), actors[1].Owner, actors[1].Session, peerSender);
            void Show(GameConnectionContext target, RaidActor actor)
            {
                var view = battle.View;
                RaidCombatProjection.PostState(target, view, actor,
                    () => actor.Session.CanPublishRaidBattle(actor, view), "fixture_luke_p2_movie");
            }
            Show(context, actors[0]); Show(peerContext, actors[1]);
            await Task.WhenAll(sender.DrainAsync(), peerSender.DrainAsync());
            That(context.RaidDisplay is { Phase: 1, State: AntonRaidBattleState.Reward } &&
                battle.CaptureLukeMovieCards(actors[0], 1) is null && battle.CaptureLukeMovieCards(actors[1], 1) is null,
                "实际电影状态写出仍不替任意有资格P2成员确认");
            lock (gate) { frames.Clear(); peerFrames.Clear(); }
            var request = actors[0].Session.RaidMovieSnapshot()!.Value;
            var team = LukeRewardPackets.TeamResults(battle.LukeTeamEntitlements(request.View));
            var fact = recovery.TryCaptureForDisconnect(actors[2].Session, out var captureReason);
            That(fact is not null && captureReason == "captured", "真实断线前捕获原Actor及Party槽位");
            actors[2].Session.End(); parties.Detach(actors[2].Session); actors[2].Owner.Dispose();
            That(recovery.TryRecordDisconnected(fact!, out var reason) && reason == "recorded", "旧owner释放后登记真实恢复预约");
            var fresh = Login(2, false);
            string assets = Assets(database);
            held.Arm = true;
            Task blocker = sender.Send(1, 1347, [1], "fixture_luke_p2_hold");
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                actors[0].Session.CommandGate.Wait();
                try
                {
                    That(RaidCombatProjection.Movie(context, actors[0], battle, request.View, request.Event, true),
                        "真实P2确认已返回，开牌帧只入队且发送泵仍被持住");
                }
                finally { actors[0].Session.CommandGate.Release(); }
                var confirmed = battle.View;
                fresh.Session.CommandGate.Wait();
                LukeRaidRejoinResult restored;
                try { restored = recovery.Restore(fresh, fact!.Key, callerCommandHeld: true); }
                finally { fresh.Session.CommandGate.Release(); }
                That(restored.Accepted && ReferenceEquals(fresh.Session.CurrentRaidBattle(fresh), battle) &&
                    battle.View.Revision > confirmed.Revision && battle.View.Phase == 1 && battle.View.State == AntonRaidBattleState.Reward &&
                    battle.CaptureMovie(actors[0])?.Event == request.Event && !battle.IsCurrent(request.View),
                    "原Restore在本人命令门释放后推进同phase版本，事件和Reward保持，原exact view仍失效");
                // 不给已确认者补PostState，防止补偿掩盖原Movie入队帧仍被旧revision吞掉。
                Show(peerContext, actors[1]);
                That(battle.CaptureLukeMovieCards(fresh, 1) is null && battle.CaptureLukeMovieCards(actors[1], 1) is null,
                    "恢复身份与别人确认均不代替本人P2完成");
                That(battle.CaptureLukeMovieCards(actors[2], 1) is null &&
                    battle.CaptureLukeMovieCards(actors[0], 0) is null &&
                    battle.CaptureLukeMovieCards(actors[0], 1, Guid.NewGuid()) is null,
                    "原owner已失效的旧actor、旧phase和错误event均无开牌快照");
            }
            finally { held.Release.TrySetResult(); }
            await blocker;
            await Task.WhenAll(sender.DrainAsync(), peerSender.DrainAsync());
            var written = Snapshot(frames);
            int teamAt = Array.FindIndex(written, row => row is { Kind: 0, Opcode: 600 } && row.Body.AsSpan().SequenceEqual(team));
            int openAt = Array.FindIndex(written, row => row is { Kind: 0, Opcode: 587 } && row.Body.AsSpan().SequenceEqual(new byte[] { 4, 1 }));
            That(teamAt >= 0 && openAt > teamAt && written.Count(row => row is { Kind: 0, Opcode: 600 } && row.Body.AsSpan().SequenceEqual(team)) == 1 &&
                written.Count(row => row is { Kind: 0, Opcode: 587 }) == 1 &&
                written.Where(row => row is { Kind: 0, Opcode: 600 } && row.Body.Length == 2 && row.Body[1] == 0)
                    .Select(row => row.Body[0]).SequenceEqual(policy.Completion.Phases[1].Rewards.Offers.Select(offer => offer.Kind)) &&
                written.Where(row => row is { Kind: 0, Opcode: 600 } && row.Body.Length == 2).All(row => Array.IndexOf(written, row) > openAt),
                "跨真实恢复版本仍完整团队页先于唯一state4，再写完整两kind选择，不需重发659");
            That(!Snapshot(peerFrames).Any(row => row is { Kind: 0, Opcode: 600 } || row is { Kind: 0, Opcode: 587 } && row.Body[0] == 4),
                "未确认成员不会由同阶段补偿提前开牌");
            int countBefore = written.Length;
            var latest = actors[0].Session.RaidMovieSnapshot()!.Value;
            That(RaidCombatProjection.Movie(context, actors[0], battle, latest.View, Guid.NewGuid(), true), "错event按原Handled拒绝入口");
            await sender.DrainAsync();
            That(Snapshot(frames).Length == countBefore, "错误event不接受确认或排任何成功帧");
            Show(context, actors[0]);
            await sender.DrainAsync();
            var broadcast = Snapshot(frames);
            That(broadcast[countBefore..] is [var coins] && coins is { Kind: 0, Opcode: 778 } &&
                coins.Body.AsSpan().SequenceEqual(RaidCombatPackets.PhaseCoins(battle.View.LukePhaseCoinsUsed!.Value)),
                "同阶段广播仅补合法复活币信息，不重发587/600牌面或确认ACK");
            That(Assets(database) == assets, "错误event及重复广播不改变已核的次数、奖励收据、库存core及钱包");
            countBefore = broadcast.Length;
            That(RaidCombatProjection.Movie(context, actors[0], battle, latest.View, latest.Event, true), "重复合法P2确认可幂等ACK");
            await sender.DrainAsync();
            var replay = Snapshot(frames)[countBefore..];
            That(replay.Length == 1 && replay[0] is { Kind: 1, Opcode: 659 } && replay[0].Body.AsSpan().SequenceEqual(new byte[] { 1 }) &&
                Assets(database) == assets,
                "重复确认只补本人ACK，不再发送团队表或牌面");

            // 仅提交原Session的659完成事实与ACK，显式省略直接开牌投影，单独验证PostState补偿。
            // 这是低层确认夹具，不把它描述为自然TCP请求或新的资产领取。
            var peerRequest = actors[1].Session.RaidMovieSnapshot()!.Value;
            actors[1].Session.CommandGate.Wait();
            try
            {
                That(actors[1].Session.ConfirmRaidMovie(actors[1], battle, peerRequest.View, peerRequest.Event, true,
                    () => peerContext.RaidDisplay == RaidCombatDisplayIdentity.Capture(actors[1], peerRequest.View),
                    () => peerSender.TryPost(1, 659, [1], "fixture_luke_p2_compensation_ack")) is { Changed: true, Acknowledged: true },
                    "未确认成员须先在原Session/owner/Battle边界接受本人的真实阶段确认");
            }
            finally { actors[1].Session.CommandGate.Release(); }
            Show(peerContext, actors[1]);
            await peerSender.DrainAsync();
            var compensated = Snapshot(peerFrames);
            int peerTeamAt = Array.FindIndex(compensated, row => row is { Kind: 0, Opcode: 600 } && row.Body.AsSpan().SequenceEqual(team));
            int peerOpenAt = Array.FindIndex(compensated, row => row is { Kind: 0, Opcode: 587 } && row.Body.AsSpan().SequenceEqual(new byte[] { 4, 1 }));
            That(peerTeamAt >= 0 && peerOpenAt > peerTeamAt && compensated.Count(row => row is { Kind: 0, Opcode: 587 }) == 1 &&
                compensated.Count(row => row is { Kind: 0, Opcode: 600 } && row.Body.AsSpan().SequenceEqual(team)) == 1 &&
                compensated.Where(row => row is { Kind: 0, Opcode: 600 } && row.Body.Length == 2)
                    .Select(row => row.Body[0]).SequenceEqual(policy.Completion.Phases[1].Rewards.Offers.Select(offer => offer.Kind)) &&
                compensated.Where(row => row is { Kind: 0, Opcode: 600 } && row.Body.Length == 2)
                    .All(row => Array.IndexOf(compensated, row) > peerOpenAt) &&
                Assets(database) == assets,
                "PostState只在本人确认后独立补齐完整团队页及两kind，先团队页再唯一开牌且不改资产");
            passed = true;

            (byte Kind, ushort Opcode, byte[] Body)[] Snapshot(List<(byte Kind, ushort Opcode, byte[] Body)> source)
            { lock (gate) return source.ToArray(); }
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
            if (passed) TestFiles.DeleteDirectory(root, true);
        }
    }

    static string Assets(string path)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        db.Open();
        var rows = new List<string>();
        foreach (string sql in new[] { "SELECT * FROM character_raid_runs", "SELECT * FROM character_raid_reward_receipts",
            "SELECT item_uid,character_id,account,item_core FROM character_inventory_items", "SELECT * FROM account_wallets" })
        {
            using var command = db.CreateCommand(); command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(sql + ":" + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        }
        return string.Join('\n', rows.Order(StringComparer.Ordinal));
    }

    sealed class HeldWriteStream : MemoryStream
    {
        internal volatile bool Arm;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Arm)
            {
                Arm = false; Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
