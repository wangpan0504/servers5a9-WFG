using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019CompetitionTests
{
    public static void RunCore()
    {
        int before = Count;
        OpeningBarriers();
        FrozenRoster();
        EnergyAndSharedSkills();
        CatalogRulesAndRouteChange();
        RealClearAndReplay();
        TimeoutAndExit();
        TerminalRace();
        BothClearRace();
        Console.WriteLine($"PASS {Count - before} 首期强者之路双队原Run、屏障、技能与唯一终局");
    }

    static void OpeningBarriers()
    {
        var f = new Fixture();
        var c = f.Competition;
        That(c.Snapshot.Phase == Strongway2019CompetitionPhase.Opening, "两队入场后仍处于开场阶段");
        That(c.ConfirmOpening(f.Run(0), f.Identity(0), 4) == DungeonRunOutcome.Rejected,
            "未完成双队phase3，不能抢先提交phase4");
        That(c.ConfirmOpening(f.Run(0), f.Identity(0), 3) == DungeonRunOutcome.Accepted &&
            c.ConfirmOpening(f.Run(1), f.Identity(1), 3) == DungeonRunOutcome.Duplicate &&
            c.Snapshot.Phase3Sides.SequenceEqual([0]), "同队四名成员只能贡献一个phase3来源");
        That(c.ConfirmOpening(f.Run(4), f.Identity(4), 3) == DungeonRunOutcome.Accepted &&
            c.Snapshot.Phase3Sides.Order().SequenceEqual([0, 1]), "phase3必须收齐两个不同队来源");
        That(c.ConfirmOpening(f.Run(0), f.Identity(0), 4) == DungeonRunOutcome.Accepted &&
            c.Snapshot.Phase == Strongway2019CompetitionPhase.Opening, "单队phase4不能启动战斗计时");
        f.Clock.Advance(700_000);
        c.Tick();
        That(c.Snapshot.Terminal is null, "600秒预算不从排队或开场等待开始消耗");
        That(c.ConfirmOpening(f.Run(4), f.Identity(4), 4) == DungeonRunOutcome.Accepted &&
            c.Snapshot.Phase == Strongway2019CompetitionPhase.Running && c.Snapshot.RemainingMilliseconds == 600_000,
            "双队phase4后才启动服务器明确注入的600秒战斗预算");
        That(c.ConfirmOpening(f.Run(5), f.Identity(5), 4) == DungeonRunOutcome.Duplicate,
            "同队phase4重放不重启计时或重复推进");
    }

    static void FrozenRoster()
    {
        var f = new Fixture();
        var original = f.Competition.Participants;
        That(original.Length == 8 && original.Select(x => x.Identity.CharacterId).Distinct().Count() == 8 &&
            original.All(x => ReferenceEquals(x.Run, f.Run(x.Identity.CharacterId - 1))),
            "聚合冻结八个不同角色，成员仍直接引用正式Run");
        var duplicate = f.Team(1); duplicate[3] = f.Run(0);
        Reject<ArgumentException>(() => f.Create(f.Team(0), duplicate), "跨队重复角色不能创建比赛");
        Reject<ArgumentException>(() => f.Create(f.Team(0)[..3], f.Team(1)), "任一队不足四人拒绝");
        var replacement = Fixture.CreateRun(1, Guid.NewGuid(), 2);
        That(f.Competition.ConfirmOpening(replacement.Run, replacement.Run.Snapshot.Identity, 3) == DungeonRunOutcome.Rejected &&
            f.Competition.ConfirmOpening(f.Run(0), f.Identity(0) with { InstanceId = Guid.NewGuid() }, 3) == DungeonRunOutcome.Rejected &&
            f.Competition.ConfirmOpening(f.Run(0), f.Identity(0) with { TaskAttempt = 2 }, 3) == DungeonRunOutcome.Rejected,
            "同角色新Run、过期实例或错误尝试不能冒充被冻结成员");
        var supplied = f.Team(0); var isolated = f.Create(supplied, f.Team(1)); supplied[0] = replacement.Run;
        That(ReferenceEquals(isolated.Participants[0].Run, f.Run(0)), "调用方随后改队伍数组不能改变已冻结成员");
    }

    static void EnergyAndSharedSkills()
    {
        var f = new Fixture(); f.Start();
        f.Clock.Advance(999); f.Competition.Tick();
        That(f.Competition.Snapshot.Energy.SequenceEqual([0, 0]), "不足一秒不提前充能");
        f.Clock.Advance(1); f.Competition.Tick();
        That(f.Competition.Snapshot.Energy.SequenceEqual([1, 1]), "两队按真实已过整秒各充能一点");
        f.Clock.Advance(99_000); f.Competition.Tick();
        That(f.Competition.Snapshot.Energy.SequenceEqual([100, 100]), "延迟Tick补足经过秒数且充能上限100");
        var first = f.Competition.UseSkill(f.Run(0), f.Identity(0), 1, 1, uint.MaxValue);
        That(first is { Outcome: DungeonRunOutcome.Accepted, Effect: { SourceSide: 0, TargetSide: 1, Kind: 1, Value: 0, Symbol: "dplitem1" } } &&
            f.Competition.Snapshot.Energy.SequenceEqual([70, 100]), "技能只扣来源队30能量，第三个客户端栈值不影响服务端规范值");
        That(f.Competition.UseSkill(f.Run(1), f.Identity(1), 1, 2, 0).Outcome == DungeonRunOutcome.Rejected,
            "全队共享10秒冷却，不能换成员绕过");
        That(f.Competition.UseSkill(f.Run(4), f.Identity(4), 0, 0, 0).Outcome == DungeonRunOutcome.Accepted,
            "另一队的冷却及免费首技能独立");
        f.Clock.Advance(9_999);
        That(f.Competition.UseSkill(f.Run(2), f.Identity(2), 1, 0, 0).Outcome == DungeonRunOutcome.Rejected,
            "共享冷却未满10秒仍拒绝");
        f.Clock.Advance(1);
        That(f.Competition.UseSkill(f.Run(2), f.Identity(2), 1, 0, 12345).Outcome == DungeonRunOutcome.Accepted,
            "正好10秒可以使用免费首技能");
        f.Clock.Advance(10_000);
        That(f.Competition.UseSkill(f.Run(3), f.Identity(3), 1, 0, 0).Outcome == DungeonRunOutcome.Rejected &&
            f.Competition.UseSkill(f.Run(0), f.Identity(0), 0, 1, 0).Outcome == DungeonRunOutcome.Rejected,
            "kind0每队限一次，所有技能均拒绝本队目标");
        That(f.Competition.UseSkill(f.Run(0), f.Identity(0), 1, 2, 0).Outcome == DungeonRunOutcome.Accepted,
            "kind2在足额能量下可使用");
        f.Clock.Advance(10_000);
        That(f.Competition.UseSkill(f.Run(0), f.Identity(0), 1, 3, 0).Outcome == DungeonRunOutcome.Rejected,
            "kind3需要60点，余额不足不能仅凭冷却结束放行");
    }

    static void CatalogRulesAndRouteChange()
    {
        var f = new Fixture(pointOne: 7, cooltime: 2_000); f.Start();
        f.Clock.Advance(10_000); f.Competition.Tick();
        That(f.Competition.UseSkill(f.Run(0), f.Identity(0), 1, 1, 500).Outcome == DungeonRunOutcome.Accepted &&
            f.Competition.Snapshot.Energy[0] == 3, "消耗读取真实Catalog行，不能在竞赛里另存固定30");
        f.Clock.Advance(2_000);
        That(f.Competition.UseSkill(f.Run(0), f.Identity(0), 1, 0, 0).Outcome == DungeonRunOutcome.Accepted,
            "共享冷却同样读取Catalog而非平行硬编码");
        var map = new Fixture(withNextMap: true); map.Start(); map.Clock.Advance(100_000);
        var result = map.Competition.UseSkill(map.Run(0), map.Identity(0), 1, 4, 109008475);
        That(result is { Outcome: DungeonRunOutcome.Accepted, Effect: { Kind: 4, Value: 400597, TargetSide: 1, Symbol: "dplitem4" } },
            "kind4从服务器随机索引选择Catalog.RandomMapIds，客户端怪物值无效");
        var pending = map.Competition.Snapshot.PendingRouteChanges.Single();
        That(pending.MapId == 400597 && pending.TargetSide == 1 && pending.Participants.Length == 4 &&
            pending.Participants.All(x => x.Side == 1 && ReferenceEquals(x.Run, map.Run(x.Identity.CharacterId - 1))),
            "换图成功必须留下对方四个正式Run的待应用路线，不止更新HUD");
        That(map.Competition.ConfirmRouteApplied(map.Run(0), map.Identity(0), pending.Id) == DungeonRunOutcome.Rejected,
            "来源队不能确认对方路线已应用");
        That(map.Competition.ConfirmRouteApplied(map.Run(4), map.Identity(4), pending.Id) == DungeonRunOutcome.Rejected,
            "只有HUD换图通知，原Run尚未进入目标地图时不能确认应用");
        for (int i = 4; i < 8; i++)
        {
            map.EnterNextMap(i);
            That(map.Competition.ConfirmRouteApplied(map.Run(i), map.Identity(i), pending.Id) == DungeonRunOutcome.Accepted,
                "正式房间推进完成后逐个确认原Run的待应用路线");
        }
        That(map.Competition.Snapshot.PendingRouteChanges.IsEmpty &&
            map.Competition.ConfirmRouteApplied(map.Run(4), map.Identity(4), pending.Id) == DungeonRunOutcome.Duplicate,
            "四个Run实际应用后清除待应用状态，重放不能重复换图");
    }

    static void RealClearAndReplay()
    {
        var f = new Fixture(); f.Start();
        var fact = f.Clear(0);
        That(f.Competition.ObserveClear(f.Run(0), f.Identity(0), fact with { }) == DungeonRunOutcome.Rejected,
            "即使字段相等，复制的通关事实也不能裁决比赛");
        That(f.Competition.ObserveClear(f.Run(0), f.Identity(0) with { TaskAttempt = 2 }, fact) == DungeonRunOutcome.Rejected,
            "原事实搭配错误尝试拒绝");
        That(f.Competition.ObserveClear(f.Run(0), f.Identity(0), fact) == DungeonRunOutcome.Accepted,
            "原Run实际提交的原始终点事实裁定胜利");
        var terminal = f.Competition.Snapshot.Terminal!;
        That(terminal is { Reason: Strongway2019FinishReason.Clear, WinnerSide: 0 } && ReferenceEquals(terminal.ClearFact, fact) &&
            terminal.RewardParticipants.Length == 4 && terminal.RewardParticipants.All(x => x.Side == 0),
            "终局保留原ClearFact引用，只有胜方四个冻结成员有奖励资格");
        That(f.Competition.ObserveClear(f.Run(0), f.Identity(0), fact) == DungeonRunOutcome.Duplicate &&
            f.Competition.Exit(f.Run(4), f.Identity(4)) == DungeonRunOutcome.Rejected,
            "原终点重放幂等，之后退出不能改写胜方");
        f.Clock.Advance(600_000); f.Competition.Tick();
        That(ReferenceEquals(f.Competition.Snapshot.Terminal, terminal), "终局后超时不覆盖同一不可变裁决");
    }

    static void TimeoutAndExit()
    {
        var f = new Fixture(); f.Start(); f.Clock.Advance(599_999); f.Competition.Tick();
        That(f.Competition.Snapshot.Terminal is null && f.Competition.Snapshot.RemainingMilliseconds == 1,
            "600秒边界前仍可战斗");
        var fact = f.Clear(0); f.Clock.Advance(1);
        That(f.Competition.ObserveClear(f.Run(0), f.Identity(0), fact) == DungeonRunOutcome.Rejected &&
            f.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Timeout, WinnerSide: null, RewardParticipants.IsEmpty: true },
            "到截止瞬间即使Tick尚未调度，迟到清图仍按双败结算且无奖励");
        var exit = new Fixture(); exit.Start();
        That(exit.Competition.Exit(exit.Run(0), exit.Identity(0)) == DungeonRunOutcome.Accepted &&
            exit.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Exit, WinnerSide: 1 } terminal &&
            terminal.ClearFact is null && terminal.RewardParticipants.Length == 4 &&
            terminal.RewardParticipants.All(row => row.Side == 1),
            "成员退出冻结败方及对侧四名胜方奖励资格，不伪造通关事实");
        That(exit.Competition.UseSkill(exit.Run(4), exit.Identity(4), 0, 0, 0).Outcome == DungeonRunOutcome.Rejected,
            "终局后禁止继续提交技能效果");
    }

    static void TerminalRace()
    {
        var f = new Fixture(); f.Start(); var fact = f.Clear(0);
        DungeonRunOutcome clear = DungeonRunOutcome.Rejected, exit = DungeonRunOutcome.Rejected;
        Parallel.Invoke(() => clear = f.Competition.ObserveClear(f.Run(0), f.Identity(0), fact),
            () => exit = f.Competition.Exit(f.Run(4), f.Identity(4)));
        var terminal = f.Competition.Snapshot.Terminal;
        That(new[] { clear, exit }.Count(x => x == DungeonRunOutcome.Accepted) == 1 && terminal is not null,
            "真实清图和对方退出并发只能提交一个终局");
        f.Clock.Advance(600_000); f.Competition.Tick();
        That(ReferenceEquals(f.Competition.Snapshot.Terminal, terminal), "后续超时仍保留竞争得到的唯一终局引用");
        var expired = new Fixture(); expired.Start(); var lateFact = expired.Clear(0);
        expired.Clock.Advance(600_000);
        Parallel.Invoke(() => clear = expired.Competition.ObserveClear(expired.Run(0), expired.Identity(0), lateFact),
            () => exit = expired.Competition.Exit(expired.Run(4), expired.Identity(4)), expired.Competition.Tick);
        That(clear == DungeonRunOutcome.Rejected && exit == DungeonRunOutcome.Rejected &&
            expired.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Timeout, WinnerSide: null, RewardParticipants.IsEmpty: true },
            "截止瞬间清图、退出及Tick三方竞争始终得到同一个双败无奖励终局");
    }

    internal static SinglePlayerDungeonRun CreateRewardRun(ushort character, Guid instance, long attempt) =>
        Fixture.CreateRun(character, instance, attempt).Run;

    sealed class Fixture
    {
        readonly (SinglePlayerDungeonRun Run, Authority Authority, ushort Monster)[] runs;
        readonly Strongway2019Catalog catalog;
        public Clock Clock { get; } = new();
        public Strongway2019Competition Competition { get; }
        public Fixture(int pointOne = 30, int cooltime = 10_000, bool withNextMap = false)
        {
            catalog = Catalog(pointOne, cooltime);
            Guid[] instances = [Guid.NewGuid(), Guid.NewGuid()];
            runs = Enumerable.Range(0, 8).Select(i => CreateRun(checked((ushort)(i + 1)), instances[i / 4], 1, withNextMap)).ToArray();
            Competition = Create(Team(0), Team(1));
        }
        public SinglePlayerDungeonRun Run(int i) => runs[i].Run;
        public DungeonRunIdentity Identity(int i) => Run(i).Snapshot.Identity;
        public SinglePlayerDungeonRun[] Team(int side) => runs.Skip(side * 4).Take(4).Select(x => x.Run).ToArray();
        public Strongway2019Competition Create(IReadOnlyList<SinglePlayerDungeonRun> first, IReadOnlyList<SinglePlayerDungeonRun> second) =>
            new(Guid.NewGuid(), first, second, catalog, TimeSpan.FromSeconds(600), Clock, count => 1);
        public void Start()
        {
            Competition.ConfirmOpening(Run(0), Identity(0), 3); Competition.ConfirmOpening(Run(4), Identity(4), 3);
            Competition.ConfirmOpening(Run(0), Identity(0), 4); Competition.ConfirmOpening(Run(4), Identity(4), 4);
        }
        public DungeonClearFact Clear(int i)
        {
            var death = Run(i).PrepareConfirmedDeath(runs[i].Authority.Death(Identity(i), runs[i].Monster)).Pending;
            That(death is not null, "真实正式Run准备权威末怪死亡");
            var result = Run(i).ConfirmCommittedDeath(death!);
            That(result.ClearFact is not null && ReferenceEquals(Run(i).ClearFact, result.ClearFact),
                "真实正式Run确认死亡后产生原始ClearFact");
            return result.ClearFact!;
        }
        public void EnterNextMap(int i)
        {
            var death = Run(i).PrepareConfirmedDeath(runs[i].Authority.Death(Identity(i), runs[i].Monster)).Pending;
            That(death is not null, "换图测试由原Run准备真实清房死亡");
            var clear = Run(i).ConfirmCommittedDeath(death!);
            That(clear.Outcome == DungeonRunOutcome.Accepted && clear.Snapshot.LivingMonsters.Count == 0 && clear.ClearFact is null,
                "原Run实际提交末怪死亡并清房，不手工声明副本通关");
            That(Run(i).Move(Identity(i), Guid.NewGuid(), new(1, 0)).Outcome == DungeonRunOutcome.Accepted &&
                Run(i).CompleteLoading(Identity(i), Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted &&
                Run(i).Snapshot.CurrentMap.Id == 400597, "原Run真实推进并加载服务器选中的下一张正式地图");
        }
        public static (SinglePlayerDungeonRun Run, Authority Authority, ushort Monster) CreateRun(ushort character, Guid instance, long attempt,
            bool withNextMap = false)
        {
            var map = new DungeonMap(withNextMap ? "map" : "boss", new(0, 0), 1, "map/strongway-competition-fixture.map");
            var nextMap = new DungeonMap("boss", new(1, 0), 400597, "map/strongway-competition-next.map");
            DungeonMap[] maps = withNextMap ? [map, nextMap] : [map];
            var maze = new DungeonMaze(0, withNextMap ? 2 : 1, 1, "strongway-competition-fixture", [map.Position], [maps[^1].Position], maps, []);
            var entry = new DungeonEntryPlan(character, 1, 5360, 0, byte.MaxValue, 0, maze, map, maps[^1])
                { DisableMonsterAndClearExperience = true, DisableFatigueConsumption = true };
            var monster = new DungeonRoomMonster(1, [1, 0, 95, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/strongway-fixture.mob");
            var plan = DungeonMonsterPlan.FromRoom(new(map, [monster], [], []), character, 100);
            var nextPlan = DungeonMonsterPlan.FromRoom(new(nextMap, [monster], [], []), character, 200);
            DungeonFixedRoute route = withNextMap ? new([plan, nextPlan], [new(map.Position, nextMap.Position)]) : new([plan], []);
            var authority = new Authority();
            That(SinglePlayerDungeonRun.TryCreate(entry, attempt, 1, route, authority, out var run, out _),
                "竞赛测试使用真实正式副本Run");
            run!.BindPartyInstance(instance);
            That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "竞赛成员先完成真实加载");
            return (run, authority, plan.Monsters.Single().ObjectKey);
        }
    }

    sealed class Authority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
    sealed class Clock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public void Advance(long value) => Interlocked.Add(ref milliseconds, value);
    }

    internal static Strongway2019Catalog Catalog(int pointOne, int cooltime)
    {
        var tokens = new List<PvfToken>(); var strings = new List<string>();
        Field("dungeon start delay", 5); Field("start area pathgate open delay", 8); Field("return village delay", 6);
        Block("normal dungeon index", [0, 5359, 4]); Block("matching dungeon index", [0, 5360, 4, 1, 5360, 4]);
        Block("reward", [3, 490012737, 1, 1789, 1795]); Field("qualifying dungeon index", 5364);
        Block("random map index", [400596, 400597, 400603]);
        Block("map monster index", [400596, 109008475, 400597, 109008143, 400603, 109008376]);
        Tag("skill system"); Field("add point", 1000, 1); Field("skill waiting time", 1500);
        string[] symbols = ["dplitem3", "dplitem1", "dplitem2", "dplitem0", "dplitem4"];
        int[] points = [0, pointOne, 45, 60, 100];
        for (int i = 0; i < 5; i++)
        {
            Tag("skill"); Field("button index", i); Tag("type"); Text(i == 4 ? "map change" : "symbol change"); Text(symbols[i]);
            Field("need point", points[i]); Field("global cooltime", cooltime); if (i == 0) Field("limit count", 1); Tag("/skill");
        }
        Tag("/skill system");
        return new(tokens, i => strings[i]);
        void Tag(string value) { tokens.Add(new(3, strings.Count)); strings.Add("[" + value + "]"); }
        void Text(string value) { tokens.Add(new(6, strings.Count)); strings.Add(value); }
        void Field(string name, params int[] values) { Tag(name); tokens.AddRange(values.Select(x => new PvfToken(0, x))); }
        void Block(string name, int[] values) { Field(name, values); Tag("/" + name); }
    }
    static void BothClearRace()
    {
        foreach (bool atDeadline in new[] { false, true })
        {
            var f = new Fixture(); f.Start();
            var firstFact = f.Clear(0); var secondFact = f.Clear(4);
            if (atDeadline) f.Clock.Advance(600_000);
            DungeonRunOutcome first = DungeonRunOutcome.Rejected, second = DungeonRunOutcome.Rejected;
            Parallel.Invoke(
                () => first = f.Competition.ObserveClear(f.Run(0), f.Identity(0), firstFact),
                () => second = f.Competition.ObserveClear(f.Run(4), f.Identity(4), secondFact));
            var terminal = f.Competition.Snapshot.Terminal;
            if (atDeadline)
            {
                That(first == DungeonRunOutcome.Rejected && second == DungeonRunOutcome.Rejected &&
                    terminal is { Reason: Strongway2019FinishReason.Timeout, WinnerSide: null,
                        ClearFact: null, RewardParticipants.IsEmpty: true, CombatElapsedMilliseconds: 600_000 },
                    "双队原Run同时提交真实通关事实到600秒边界，均不能抢过双败无奖终局");
            }
            else
            {
                int acceptedSide = first == DungeonRunOutcome.Accepted ? 0 : 1;
                var winningFact = acceptedSide == 0 ? firstFact : secondFact;
                That(new[] { first, second }.Count(result => result == DungeonRunOutcome.Accepted) == 1 &&
                    new[] { first, second }.Count(result => result == DungeonRunOutcome.Rejected) == 1 &&
                    terminal is { Reason: Strongway2019FinishReason.Clear } && terminal.WinnerSide == acceptedSide &&
                    ReferenceEquals(terminal.ClearFact, winningFact) && terminal.RewardParticipants.Length == 4 &&
                    terminal.RewardParticipants.All(member => member.Side == acceptedSide) &&
                    terminal.RewardParticipants.Select(member => member.Run).SequenceEqual(f.Team(acceptedSide)),
                    "双队原Run真实通关并发只接受一方原事实，奖励名单精确冻结该方原四人");
                That(f.Competition.ObserveClear(f.Run(acceptedSide * 4), f.Identity(acceptedSide * 4), winningFact) ==
                        DungeonRunOutcome.Duplicate &&
                    f.Competition.Exit(f.Run((1 - acceptedSide) * 4), f.Identity((1 - acceptedSide) * 4)) ==
                        DungeonRunOutcome.Rejected,
                    "胜方原通关重放幂等，败方随后退出不能改写双方通关竞争结果");
            }
            f.Clock.Advance(600_000); f.Competition.Tick();
            That(ReferenceEquals(f.Competition.Snapshot.Terminal, terminal),
                "双方通关竞争后反复推进到期仍保持同一个不可变终局引用");
        }
    }

}
