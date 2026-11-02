using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidMapEntryTcpTests
{
    internal static async Task RunBattlePassDeathAsync(ServerOptions source, string root, TcpTestResources resources, int mission, string scenario = "normal")
    {
        if (mission is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(mission));
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var definition = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main,
            resources.ItemResources.Items, antonMapPath: AntonRaidMapPaths.Bind(main.MapPath)).Find(0)!;
        await VerifyPhaseProgressAsync(source, Path.Combine(root, $"anton-bp-{mission}-{scenario}"), resources, definition, mission, scenario);
    }

    // 两个开关都打开时，沿真实 117 和 585/586 核对阶段 0 里不依赖计时到期的战况。不发送 596/597，也不进阶段 1。
    static async Task VerifyPhaseProgressAsync(ServerOptions source, string root, TcpTestResources resources, RaidDefinition definition, int deathMission = 0, string scenario = "normal")
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-phase-progress"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        LukeRaidClockTests.Clock? clock = null;
        bool deathChecked = false;
        if (deathMission != 0)
        {
            var start = DateTimeOffset.Parse("2026-10-29T06:00:00+08:00");
            var policy = new BattlePassMissionPolicy("anton-death-fixture", start, BattlePassCatalog.Load(resources.Archive));
            var at = Enumerable.Range(0, policy.Days).Select(day => start.AddDays(day).AddHours(2))
                .First(time => policy.Active(time).Any(row => row.Key.Category == 0 && row.Key.Id == deathMission));
            clock = new LukeRaidClockTests.Clock(at);
            repository.ConfigureBattlePassMissions(policy);
        }
        await using var raidClocks = scenario == "writer" ? new LukeRaidClocks(clock!, repository, (_, error) => throw error) : null;
        var owners = new AccountOwners();
        var raids = new RaidRegistry();
        var parties = new PartyService(repository);
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var catalog = definition.Resources!.EntryCatalog!;
        var entry = new AntonRaidDungeonService(raids, parties, catalog, true);
        var service = new PartyDungeonService(parties, repository);
        var stats = new CharacterStatComputer(resources.Characters);
        using var log = new ServerLog(fixture.Options.LogDirectory);
        var town = new TownProjection(resources.Towns, stats, null, resources.ItemResources.EpicPieces);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var levels = new CharacterLevelProjection(stats);
        var handler = new DungeonHandler(log, main, rooms, resources.Growth, kills, town, levels,
            partyDungeons: service, partyStats: stats, parties: parties, antonRaid: entry);
        var commands = new GameCommandRegistry();
        new DungeonSelectionHandler(town, log, dungeons: main, partyDungeons: service, antonRaid: entry).Register(commands);
        handler.Register(commands);
        new DungeonSettlementHandler(log, main, SelfTestCatalogs.ClearExperience(resources.Archive), resources.Growth, town, rooms,
            SelfTestCatalogs.MonsterDrops(resources.Archive), resources.ItemResources.Items, levels).Register(commands);
        new KeepaliveHandler(log).Register(commands);
        commands.Seal();
        var sessions = new List<GameSession>();
        var leases = new List<AccountOwners.Lease>();
        var peers = new List<TcpPeer>();
        var workers = new List<Task>();
        var displayedStates = new Dictionary<int, byte>();
        RaidActor[] actors = [];
        using var stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                var owner = owners.Acquire("antonphase" + i, () => { });
                leases.Add(owner);
                var origin = new Location(38, 0, 629, 196);
                var candidate = CharacterCreation.Prepare("AntPhase" + i, resources.Characters.Get(0), origin, origin, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
                var character = repository.Create(owner, candidate with { Level = 85, AcceptedStoryLevel = 85,
                    CompletedQuests = [12829, 3779], InitialLearning = learning });
                InventoryService.Commit(repository, owner, character,
                    new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces)
                        .Grant(character, [new(10096296, 1)]));
                var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 19), 23, "[raid]"), timeProvider: clock);
                session.Select(0); session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
                session.HasMultiplePartyMembers = () => parties.HasMultipleMembers(session);
                sessions.Add(session);
            }
            actors = sessions.Select(session => session.CaptureRaidActor(raids)!).ToArray();
            raids.Create(actors[0], 0, "phase", "phase"u8.ToArray(), 20, 1);
            raids.Invite(actors[0], actors[1], 0);
            That(raids.Respond(actors[1], actors[0].Character, true).Accepted, "阶段推进夹具加入原团");
            raids.Assign(actors[0], actors[0].Character, 1);
            raids.Assign(actors[0], actors[1].Character, 1);
            var raidRewards = scenario == "writer" ? new AntonResourceRewards(definition.Resources!.Script,
                resources.ItemResources.Items, resources.ItemResources.Boosters) : null;
            var start = new AntonRaidStart(raids, parties, repository, resources.Towns, resources.ItemResources.EpicPieces,
                clock: clock, phaseProgress: true, rewards: raidRewards, timers: raidClocks);
            That(start.Start(actors[0], raids.Find(actors[0])!, definition with { StartMinimum = 1 }).Receipt is not null &&
                actors[0].Session.CurrentRaidBattle(actors[0]) is { PhaseProgress: true },
                "真实脚本开战打开阶段推进，仍然没有奖励策略");
            for (int i = 0; i < sessions.Count; i++)
            {
                var accept = listener.AcceptTcpClientAsync(stop.Token);
                peers.Add(new TcpPeer(((IPEndPoint)listener.LocalEndpoint).Port, channelNumber: 19));
                workers.Add(ServeAsync(await accept, leases[i], sessions[i]));
            }
            listener.Stop();

            if (deathMission != 0)
            {
                ClearDungeon(210);
                That(deathChecked, "正式安徒恩路径必须包含符合任务条件的真实敌方怪物");
                return;
            }

            for (int remaining = 3; remaining > 0; remaining--)
            {
                var partial = ClearDungeon(210);
                That(StateOf(210) == 0 && StateOf(212) == 2 &&
                    sessions[0].RaidCombatView()!.Symbols["[210 dgn hp]"] == remaining,
                    "双人Run仅扣一次HP，前三轮不开放后续图");
                That(!MovieValues(partial).Any(), "前三次黑雾不发送零值影片事件");
                var symbols = partial.Last(packet => packet is { Kind: 0, Opcode: 583 }).Body;
                That(Enumerable.Range(0, symbols[0]).Any(index =>
                    BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(1 + 8 * index)) == 50 &&
                    BinaryPrimitives.ReadInt32LittleEndian(symbols.AsSpan(5 + 8 * index)) == remaining),
                    "583真实symbol50按3/2/1同步HP，不以包存在代替字段验证");
                if (remaining == 3)
                    Bytes(partial.Last(packet => packet is { Kind: 0, Opcode: 587 }).Body, [2, 0], "首个Run只发布Fighting/0");
                LeaveBoth();
            }
            var afterBlack = ClearDungeon(210);
            That(MovieValues(afterBlack).SequenceEqual([(122, 1)]), "黑雾第四次仅触发一次破雾影片122");
            AssertStates(afterBlack, new()
            {
                [210] = 2, [211] = 1, [212] = 0, [213] = 0, [214] = 0, [215] = 0, [216] = 2
            }, fighting: true);
            LeaveBoth();
            peers[0].Send(15, new byte[4]);
            ReadPhase(peers[0]); ReadPhase(peers[1]);
            peers[0].Send(16, Request(211));
            That(ReadPhase(peers[0]).Any(packet => packet is { Kind: 1, Opcode: 16 }) && sessions[0].DungeonSnapshot is null,
                "211 仍是 close，16 拒绝且没有 Run");

            var meteoBattle = sessions[0].CurrentRaidBattle(actors[0])!;
            meteoBattle.Publish(meteoBattle.View);
            That(!MovieValues(ReadPhase(peers[0])).Any(), "相同revision不重播影片");
            ReadPhase(peers[1]);
            meteoBattle.SetMechanism(meteoBattle.Instance, meteoBattle.View.Revision, "[movie smoke out]", 0);
            meteoBattle.Publish(meteoBattle.View);
            That(!MovieValues(ReadPhase(peers[0])).Any(), "归零只复位，不发送会触发影片的零值");
            ReadPhase(peers[1]);
            meteoBattle.SetMechanism(meteoBattle.Instance, meteoBattle.View.Revision, "[movie smoke out]", 1);
            meteoBattle.Publish(meteoBattle.View);
            That(MovieValues(ReadPhase(peers[0])).SequenceEqual([(122, 1)]), "归零后的下一次置位仍可播放");
            ReadPhase(peers[1]);
            long previousMovie = meteoBattle.View.AntonMovieEvents[122];
            That(meteoBattle.SetMechanism(meteoBattle.Instance, meteoBattle.View.Revision, "[movie smoke out]", 1) is not null &&
                meteoBattle.View.AntonMovieEvents[122] > previousMovie, "新的同值影片事件提交新代");
            meteoBattle.Publish(meteoBattle.View);
            That(MovieValues(ReadPhase(peers[0])).SequenceEqual([(122, 1)]), "未归零的下一次真实SET 1仍播放，不把规则事件当快照");
            ReadPhase(peers[1]);
            meteoBattle.SetMechanism(meteoBattle.Instance, meteoBattle.View.Revision, "[meteo passive create]", 1);
            meteoBattle.Publish(meteoBattle.View);
            That(MeteoValues(ReadPhase(peers[0])).SequenceEqual([1]), "陨石首次置位只发布一次2=1");
            ReadPhase(peers[1]);
            meteoBattle.Publish(meteoBattle.View);
            That(!MeteoValues(ReadPhase(peers[0])).Any(), "同revision快照重放不重播火山字幕");
            ReadPhase(peers[1]);
            var afterBlue = ClearDungeon(212);
            That(!MovieValues(afterBlue).Any(), "其它地下城清图不重播破雾影片");
            That(!MeteoValues(afterBlue).Any(), "其它地下城清图快照不重播仍为1的陨石字幕");
            That(StateOf(212) == 1 && StateOf(213) == 0, "212 clear 的实际线上状态为 close");
            LeaveBoth();
            var afterRed = ClearDungeon(214);
            That(StateOf(214) == 1 && StateOf(215) == 0, "214 clear 的实际线上状态为 close");
            LeaveBoth();
            ClearDungeon(213); LeaveBoth();
            var afterLeg = ClearDungeon(213);
            That(StateOf(213) == 3 && sessions[0].RaidCombatView() is { State: AntonRaidBattleState.Fighting, Phase: 0 } view &&
                view.Symbols["[1phase success]"] == 0, "单腿 clear 发布 Clear，阶段成功仍是 0");
            LeaveBoth();
            ClearDungeon(215); LeaveBoth();
            var afterSuccess = ClearDungeon(215);
            AssertStates(afterSuccess, new()
            {
                [210] = 2, [211] = 2, [212] = 2, [213] = 2, [214] = 2, [215] = 2, [216] = 2
            }, fighting: false);
            var done = actors[0].Session.CurrentRaidBattle(actors[0])!;
            That(done.View.Symbols["[meteo passive create]"] == 0 && MeteoValues(afterSuccess).SequenceEqual([0]) &&
                done.View.Timers.IsEmpty, "阶段结束清陨石状态并发布2=0，旧计时器不留到结算屏");
            That(done.View is { State: AntonRaidBattleState.Reward, Phase: 0 } && done.View.Symbols["[1phase success]"] == 1 &&
                done.AdvanceAfterRewards(done.Instance, done.View.Revision) is null && done.View.Phase == 0,
                "双腿 clear 后 587 进入 Reward，没有奖励策略就不能进阶段 1");
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            foreach (var peer in peers) peer.Dispose();
            await Task.WhenAll(workers);
            foreach (var session in sessions) session.End();
            foreach (var owner in leases) owner.Dispose();
        }

        static IEnumerable<(int Id, int Value)> MovieValues(IEnumerable<WirePacket> packets) => packets
            .Where(packet => packet is { Kind: 0, Opcode: 583 })
            .SelectMany(packet => Enumerable.Range(0, packet.Body[0]).Select(index => (
                Id: BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(1 + 8 * index)),
                Value: BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(5 + 8 * index)))))
            .Where(row => row.Id is >= 121 and <= 125 or 128);

        static IEnumerable<int> MeteoValues(IEnumerable<WirePacket> packets) => packets
            .Where(packet => packet is { Kind: 0, Opcode: 583 })
            .SelectMany(packet => Enumerable.Range(0, packet.Body[0]).Where(index =>
                BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(1 + 8 * index)) == 2)
                .Select(index => BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(5 + 8 * index))));

        List<WirePacket> ClearDungeon(int dungeon)
        {
            if (sessions[0].Phase != SessionPhase.MapSelect)
            {
                peers[0].Send(15, new byte[4]);
                ReadPhase(peers[0]); ReadPhase(peers[1]);
            }
            peers[0].Send(16, Request(dungeon));
            var opened = ReadPhase(peers[0]);
            ReadPhase(peers[1]);
            That(opened.Any(packet => packet is { Kind: 0, Opcode: 29 }), dungeon + " 的 16 发布真实 29");
            peers[0].Send(37, new byte[8]); peers[1].Send(37, new byte[8]);
            ReadPhase(peers[0]); ReadPhase(peers[1]);
            var scene = sessions[0].CurrentFormalDungeonScene()!;
            var route = QuestStoryRouteTests.Path(scene.Entry.Maze.StaticTopology, scene.Entry.StartMap.Position, scene.Entry.BossMap.Position);
            var party = sessions[0].PartyDungeon!;
            DungeonPlannedMonster? boss = null;
            for (int index = 0; index < route.Length; index++)
            {
                var current = sessions[0].CurrentFormalDungeonScene()!;
                foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly).ToArray())
                {
                    if (monster.IsBoss) boss = monster;
                    var observation = FormalDungeonSessionTests.Observation(current, monster);
                    bool target = deathMission != 0 && monster.MonsterId > 0 && !monster.IsSpecialChild &&
                        monster.AiCharacter is null && (deathMission == 1 ? !monster.IsBoss : monster.IsBoss);
                    var before = target ? leases.Select(owner => repository.ReadBattlePassMissions(owner, clock!.GetUtcNow())!
                        .Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == deathMission).Count).ToArray() : [];
                    var growthBefore = target ? sessions.Select(session => Convert.ToInt64(fixture.Query(
                        "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", session.Selected!.Id)[0][0])).ToArray() : [];
                    var memberScenes = target ? sessions.Select(session => session.CurrentFormalDungeonScene()).ToArray() : [];
                    SinglePlayerDungeonRun? targetRun = null;
                    Guid? rollbackEvent = null;
                    if (target)
                    {
                        That(before.All(count => count < (deathMission == 1 ? 10 : 2)), "目标死亡前两账号任务均已排期且未达上限");
                        That(memberScenes.All(scene => scene is not null), "死亡前两成员均有当前正式场景");
                        for (int member = 0; member < sessions.Count; member++)
                        {
                            var own = memberScenes[member]!;
                            That(own.Snapshot.Identity.CharacterId == sessions[member].Selected!.Id &&
                                own.Snapshot.Identity.InstanceId == current.Snapshot.Identity.InstanceId &&
                                own.Snapshot.CurrentMap == current.Snapshot.CurrentMap &&
                                own.Monsters.Monsters.Any(row => row.ObjectKey == monster.ObjectKey && row.MonsterId == monster.MonsterId),
                                "每名成员绑定自己的角色、共享Run与同一地图和已发布怪物");
                        }
                    }
                    PartyDungeonNotice[] Observe(NativeDungeonDeathObservation report,
                        Func<GameSession, AntonRaidBattle.RaidDeathEligibility?>? capture = null) =>
                        party.ObserveDeath(sessions[0], monster.ObjectKey,
                            member => member.ObserveFormalDungeonDeath(report, kills, resources.Growth),
                            captureRaidEligibility: capture ?? (member => member.CaptureRaidDeathEligibility(monster.ObjectKey)),
                            observeRaidDeath: (member, eligibility) => member.ObserveFormalDungeonDeath(report, kills, resources.Growth, eligibility, true));
                    if (target)
                    {
                        var formal = typeof(GameSession).GetProperty("formalDungeon", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic)!.GetValue(sessions[0])!;
                        var originalRun = (SinglePlayerDungeonRun)formal.GetType().GetProperty("Run")!.GetValue(formal)!;
                        targetRun = originalRun;
                        var boundBattle = actors[0].Session.CurrentRaidBattle(actors[0])!;
                        That(boundBattle.CaptureRaidDeathEligibility(actors[0] with { Epoch = actors[0].Epoch + 1 },
                            originalRun,monster.ObjectKey) is null, "旧选角代次不能领取死亡资格");
                        That(boundBattle.CaptureRaidDeathEligibility(actors[1],originalRun,monster.ObjectKey) is null,
                            "其他成员不能借用本角色Run领取死亡资格");
                        var invalid = Observe(observation with { ChecksumMatches = false });
                        That(!invalid.Any(notice => notice.Kind == PartyDungeonNoticeKind.Death), "非法死亡不会因预备产生成功");
                        for (int member = 0; member < sessions.Count; member++)
                        {
                            That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",
                                sessions[member].Selected!.Id)[0][0]) == growthBefore[member], "非法死亡不提交成长");
                            var memberFormal = typeof(GameSession).GetProperty("formalDungeon", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)!.GetValue(sessions[member])!;
                            var memberRun = (SinglePlayerDungeonRun)memberFormal.GetType().GetProperty("Run")!.GetValue(memberFormal)!;
                            That(typeof(SinglePlayerDungeonRun).GetField("pendingDeath", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)!.GetValue(memberRun) is null,
                                "首成员原死亡拒绝后无其他成员孤立pending");
                        }
                    }
                    PartyDungeonNotice[] notices;
                    if (target && scenario == "rollback")
                    {
                        fixture.Sql("CREATE TRIGGER anton_bp_fail BEFORE INSERT ON account_battle_pass_source_events " +
                            "WHEN NEW.source_kind='anton_raid_monster_death' BEGIN SELECT RAISE(ABORT,'anton bp rollback'); END");
                        try
                        {
                            Reject<SqliteException>(() => Observe(observation), "战令来源失败必须回滚该角色原死亡事务");
                            var pending = (PreparedDungeonDeath?)typeof(SinglePlayerDungeonRun).GetField("pendingDeath",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(targetRun);
                            That(pending is not null && pending.Death.EventId != Guid.Empty, "失败保留首成员原死亡身份");
                            rollbackEvent = pending!.Death.EventId;
                            for (int member = 0; member < sessions.Count; member++)
                            {
                                That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",
                                    sessions[member].Selected!.Id)[0][0]) == growthBefore[member], "失败未提交任何角色成长回执");
                                That(repository.ReadBattlePassMissions(leases[member],clock!.GetUtcNow())!.Slots
                                    .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == deathMission).Count == before[member],
                                    "失败未提交任务进度");
                            }
                        }
                        finally { fixture.Sql("DROP TRIGGER anton_bp_fail"); }
                        notices = Observe(observation);
                    }
                    else if (target && scenario == "prepare-retry")
                    {
                        bool interrupted = false;
                        Reject<InvalidOperationException>(() => Observe(observation, member =>
                        {
                            if (ReferenceEquals(member,sessions[1]) && !interrupted)
                            { interrupted = true; throw new InvalidOperationException("fixture prepare interruption"); }
                            return member.CaptureRaidDeathEligibility(monster.ObjectKey);
                        }), "半预备异常保留原队伍pending");
                        That(interrupted, "异常实际发生在第二成员预备前");
                        for (int member = 0; member < sessions.Count; member++)
                            That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",
                                sessions[member].Selected!.Id)[0][0]) == growthBefore[member], "预备异常不提交成长");
                        notices = Observe(observation);
                    }
                    else if (target && scenario == "writer")
                    {
                        int prepared = 0;
                        var originalWriterGate = (SemaphoreSlim)(typeof(SqliteWriteGate)
                            .GetField("Gate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                            .GetValue(null) ?? throw new InvalidOperationException("原writer gate缺失"));
                        var waiterField = typeof(SemaphoreSlim).GetField("m_waitCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?? typeof(SemaphoreSlim).GetField("_waitCount", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("原writer waiter计数不可观测");
                        Task<PartyDungeonNotice[]> queued;
                        var activeBattle = actors[0].Session.CurrentRaidBattle(actors[0])!;
                        using (var holdDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database, Pooling = false }.ToString()))
                        {
                            holdDb.Open();
                            using var heldWriter = holdDb.BeginWrite("AntonBattlePassDeath.WriterHold");
                            using var heldTransaction = heldWriter.Transaction;
                            queued = Task.Run(() => Observe(observation, member =>
                            {
                                var eligibility = member.CaptureRaidDeathEligibility(monster.ObjectKey);
                                That(eligibility is not null, "跨期限前每成员成功冻结原Raid资格");
                                Interlocked.Increment(ref prepared);
                                return eligibility;
                            }));
                            That(SpinWait.SpinUntil(() => Convert.ToInt32(waiterField.GetValue(originalWriterGate)) >= 1,
                                TimeSpan.FromSeconds(10)), "首成员原成长已在writer闸门排队");
                            That(Volatile.Read(ref prepared) == sessions.Count, "首成员写入前全员已独立冻结资格");
                            int phaseSeconds = definition.Resources!.Script.Headers.Single(row => row.Tag == "[phase time over]").Values[0].Number;
                            clock!.AdvanceWithoutCallbacks(phaseSeconds + 1);
                            That(!activeBattle.AllowsAntonEntry(210), "等待期间实际跨越原阶段期限");
                            That(activeBattle.CaptureRaidDeathEligibility(actors[0],targetRun!,monster.ObjectKey) is null,
                                "期限已到且timer尚未执行时不可新签Raid资格");
                        }
                        notices = queued.GetAwaiter().GetResult();
                    }
                    else notices = Observe(observation);
                    That(notices.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death) == 2 &&
                        notices.Where(notice => notice.Kind == PartyDungeonNoticeKind.Death)
                            .Select(notice => notice.Recipient.Actor.Character.Id).Order()
                            .SequenceEqual(sessions.Select(session => session.Selected!.Id).Order()) &&
                        notices.All(notice => notice.Kind is PartyDungeonNoticeKind.Death or PartyDungeonNoticeKind.RaidBattleSync),
                        "阶段图的阻挡怪由两名队员各自提交，终图额外同步已提交团本成果");
                    if (target)
                    {
                        for (int member = 0; member < sessions.Count; member++)
                        {
                            long growthAfter = Convert.ToInt64(fixture.Query(
                                "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", sessions[member].Selected!.Id)[0][0]);
                            That(growthAfter == growthBefore[member] + 1, "战令断言前先证明该角色的原成长死亡回执已提交");
                            var own = memberScenes[member]!;
                            var identity = own.Snapshot.Identity;
                            var receipt = fixture.Query("SELECT event_id,account FROM character_dungeon_growth_receipts " +
                                "WHERE character_id=$p0 AND account=$p1 AND instance_id=$p2 AND quest_id=$p3 " +
                                "AND task_attempt=$p4 AND epoch=$p5 AND map_id=$p6 AND monster_key=$p7 AND monster_id=$p8",
                                identity.CharacterId, leases[member].Account, identity.InstanceId.ToString("N"), identity.QuestId,
                                identity.TaskAttempt, identity.Epoch, own.Snapshot.CurrentMap.Id, monster.ObjectKey, monster.MonsterId);
                            That(receipt.Length == 1 && (string)receipt[0][1] == leases[member].Account &&
                                Guid.TryParseExact((string)receipt[0][0], "N", out var eventId) && eventId != Guid.Empty,
                                "成长回执属于本成员原身份、地图和怪物，且具有非空权威死亡事件身份");
                            if (member == 0 && rollbackEvent is { } expectedEvent)
                                That((string)receipt[0][0] == expectedEvent.ToString("N"), "失败重试沿用原死亡EventId");
                        }
                        for (int member = 0; member < sessions.Count; member++)
                        {
                            var slot = repository.ReadBattlePassMissions(leases[member], clock!.GetUtcNow())!.Slots
                                .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == deathMission);
                            That(slot.Count == before[member] + 1, $"Anton RED: active mission {deathMission} member {member} advances once");
                        }
                        Observe(observation);
                        for (int member = 0; member < sessions.Count; member++)
                        {
                            var slot = repository.ReadBattlePassMissions(leases[member],clock!.GetUtcNow())!.Slots
                                .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == deathMission);
                            That(slot.Count == before[member] + 1 && Convert.ToInt64(fixture.Query(
                                "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0", sessions[member].Selected!.Id)[0][0]) == growthBefore[member] + 1,
                                "重复死亡不重复提交任何成员成长或战令");
                        }
                        deathChecked = true;
                        return [];
                    }
                }
                if (index + 1 == route.Length) break;
                var next = route[index + 1];
                That(party.Move(sessions[0], (byte)next.X, (byte)next.Y, 0).Length == 2, "清房后两名队员进入下一房");
                That(party.CompleteLoading(sessions[0]).Length == 0 && party.CompleteLoading(sessions[1]).Length == 2, "下一房等两名队员加载");
            }
            That(boss is not null, dungeon + " 的路线上有真实 Boss");
            byte[] body = new byte[39];
            BinaryPrimitives.WriteUInt16LittleEndian(body, sessions[0].Selected!.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), boss!.ObjectKey);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), checked((uint)boss.CandidateLevel));
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(17), 504);
            peers[0].Send(117, body);
            var result = Until(peers[0], 115);
            ReadPhase(peers[1]);
            That(result.Any(packet => packet is { Kind: 0, Opcode: 585 or 586 }),
                "117 后发布实际585/586状态，实际 " + string.Join(',', result.Select(packet => packet.Kind + ":" + packet.Opcode)));
            var battle = sessions[0].CurrentRaidBattle(actors[0])!;
            foreach (var row in battle.DungeonSnapshot(battle.View))
                That(StateOf(row.Id) == (byte)row.State, "每次清图后客户端累计状态与已提交战况一致：" + row.Id);
            That(!result.Any(packet => packet.Opcode is 596 or 597 or 601), "阶段迁移不发送 596、597 或没有奖励策略时的 601");
            return result;
        }

        List<WirePacket> Until(TcpPeer peer, ushort opcode)
        {
            var rows = new List<WirePacket>();
            for (int i = 0; i < 80; i++)
            {
                var packet = peer.Read();
                rows.Add(packet);
                Observe(packet);
                if (packet is { Kind: 0, Opcode: var id } && id == opcode) return rows;
            }
            throw new InvalidOperationException("没有等到 NOTI " + opcode);
        }

        void LeaveBoth()
        {
            peers[0].Send(42); ReadPhase(peers[0]); ReadPhase(peers[1]);
            peers[1].Send(42); ReadPhase(peers[1]);
            That(sessions.All(session => session.Phase == SessionPhase.Town && session.DungeonSnapshot is null), "42 后两人都回到候战室");
        }

        async Task ServeAsync(TcpClient socket, AccountOwners.Lease owner, GameSession session)
        {
            using (socket)
            using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                var stream = socket.GetStream();
                var connection = new ConnectionContext(240 + sessions.IndexOf(session), IPAddress.Loopback, stream, lifetime);
                await using var sender = new GameConnectionSender(owner, stream, lifetime, 0, (_, _, _, _, _) => { });
                var context = new GameConnectionContext(connection, owner, session, sender);
                session.RaidBattleNoticeArrived = (view, actor) => RaidCombatProjection.Notice(context, view, actor);
                handler.PreparePartyContext(context);
                try
                {
                    while (!stop.IsCancellationRequested)
                        await commands.DispatchAsync(context, GameFrame.ParseClient(await connection.ReadAsync()));
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) when (stop.IsCancellationRequested) { }
            }
        }

        static byte[] Request(int dungeon)
        {
            byte[] request = Convert.FromHexString("D20000000200000000FFFF0000000000000000000000000000");
            BinaryPrimitives.WriteUInt32LittleEndian(request, checked((uint)dungeon));
            return request;
        }

        void AssertStates(List<WirePacket> packets, Dictionary<int, byte> expected, bool fighting)
        {
            foreach (var (id, state) in expected)
                That(StateOf(id) == state, $"585/586累计的 {id} 状态字节为 {state}");
            if (fighting)
                That(!packets.Any(packet => packet is { Kind: 0, Opcode: 587 }), "同phase Fighting不重复发布587演出");
            else
                Bytes(packets.Last(packet => packet is { Kind: 0, Opcode: 587 }).Body, [3, 0], "双腿成功后587是Reward/0");
        }

        List<WirePacket> ReadPhase(TcpPeer peer)
        {
            var packets = Read(peer);
            if (ReferenceEquals(peer, peers[0]))
                foreach (var packet in packets) Observe(packet);
            return packets;
        }

        void Observe(WirePacket packet)
        {
            if (packet is { Kind: 0, Opcode: 585 })
            {
                That(packet.Body.Length >= 6 && packet.Body.Length == 6 + 5 * packet.Body[1], "585完整条目数与包长严格一致");
                var ids = new HashSet<int>();
                for (int i = 0; i < packet.Body[1]; i++)
                {
                    int at = 2 + 5 * i;
                    int id = BinaryPrimitives.ReadInt32LittleEndian(packet.Body.AsSpan(at));
                    That(ids.Add(id), "585同帧地下城身份不重复");
                    ObserveState(id, packet.Body[at + 4]);
                }
            }
            else if (packet is { Kind: 0, Opcode: 586 })
            {
                That(packet.Body.Length == 6, "586严格6字节布局");
                ObserveState(BinaryPrimitives.ReadInt32LittleEndian(packet.Body), packet.Body[5]);
            }
        }

        void ObserveState(int dungeon, byte state)
        {
            That(state <= 3, "原生地图状态在Open/Close/Hide/Clear范围内");
            That(state != 3 || !displayedStates.TryGetValue(dungeon, out byte previous) || previous != 3,
                "585和586共享消费者，不重复发布同一Clear横幅：" + dungeon);
            displayedStates[dungeon] = state;
        }

        byte StateOf(int dungeon)
        {
            That(displayedStates.ContainsKey(dungeon), dungeon + " 缺少真实585/586初始化或变更");
            return displayedStates[dungeon];
        }
    }
}
