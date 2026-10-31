using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 预留与活跃Run共用原团本分队上限；已退出Run仅由原成员绑定引用，不保留无限游玩历史。
    sealed class RaidDungeonRun(Guid id, int phase, uint dungeon, byte squad, long generation,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors)
    {
        public Guid Id { get; } = id;
        public int Phase { get; } = phase;
        public uint Dungeon { get; } = dungeon;
        public byte Squad { get; } = squad;
        public long Generation { get; } = generation;
        public (RaidActor Actor, DungeonRunIdentity Identity)[] Actors { get; } = actors;
        public HashSet<RaidActor> Active { get; } = actors.Select(actor => actor.Actor).ToHashSet();
        public LukeRouteLease? Route { get; init; }
        public LukeRaidObjectRegistration? Objects { get; set; }
        public LukeObjectBinding? FacilityRoom { get; set; }
        public LukeObjectBinding? EnergyRoom { get; set; }
        public HashSet<LukeObjectBinding> ConsumedFacilities { get; } = [];
        public FacilityLedger? FacilityLedger { get; set; }
        public bool FacilityLedgerAmbiguous { get; set; }
        public int RevivalUsed { get; set; }
        public int? RevivalLimit { get; init; }
        public Dictionary<RaidActor, long> RevivedDeaths { get; } = [];
        public bool Installed { get; set; }
        public bool EnterConsumed { get; set; }
        public Dictionary<RaidActor, long> MapEpochs { get; } = [];
        public Dictionary<RaidActor, DungeonClearFact> ClearFacts { get; } = [];
        public HashSet<ushort> RosterContributions { get; } = [];
        public bool GuildPartyRecorded { get; set; }
        public bool Cleared => ClearFacts.Count != 0;
        public bool AntonEnergyConfirmed { get; set; }
        public long EnergyReturnEpoch { get; set; }
    }
    readonly Dictionary<Guid, RaidDungeonRun> dungeonRuns = [];
    AntonRaidBattleView? pendingTransition;
    readonly HashSet<int> endedLukePhases = [];
    public bool HasPendingTransition { get { lock (gate) return pendingTransition is not null; } }

    // 能力对象仅由原Battle签发；Dispose只撤销尚未安装的本次预留，不释放成功安装后的Run。
    internal sealed class DungeonReservation : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly RaidDungeonRun run;
        bool finished;
        internal AntonRaidBattleChange? ReleasedChange { get; private set; }
        private DungeonReservation(AntonRaidBattle battle, RaidDungeonRun run) { this.battle = battle; this.run = run; }
        internal static DungeonReservation? Prepare(AntonRaidBattle battle, RaidDungeonIdentity identity,
            uint dungeon, (RaidActor Actor, DungeonRunIdentity Identity)[] actors, LukeRaidObjectRegistration? objects, LukeRouteLease? route, int? mazeOrdinal, int? coinLimit)
        {
            lock (battle.gate)
            {
                if (objects is not null && (battle.Kind != RaidKind.Luke || objects.Dungeon != dungeon || actors.Length == 0 || objects.Run != actors[0].Identity.InstanceId) ||
                    battle.Kind == RaidKind.Fiendwar &&
                        (coinLimit is not null && coinLimit is not (> 0 and <= byte.MaxValue) ||
                         coinLimit is null && battle.fiendwarDefinition?.Guide != true) ||
                    identity.Kind != battle.Kind || identity.InstanceId != battle.Instance ||
                    battle.Kind == RaidKind.Prey && (identity.Squad != 1 || actors.Length != 1 || !battle.AllowsPreyEntry(checked((int)dungeon))) ||
                    identity.Phase != battle.current.Phase || identity.Squad < 1 || identity.Squad > (battle.Kind == RaidKind.Fiendwar ? 8 : battle.Kind == RaidKind.Luke ? 2 : 5) ||
                    battle.Kind == RaidKind.Anton && !battle.AntonEntryOpenLocked(checked((int)dungeon)) ||
                    !battle.AllowsLukeThroneLocked(checked((int)dungeon)) ||
                    TerminalRequested(battle.current) || battle.LukePhaseDueLocked() || battle.pendingTransition is not null ||
                    !battle.current.Dungeons.TryGetValue(checked((int)dungeon), out var state) || state != RaidDungeonState.Open ||
                    actors.Length is < 1 or > 4 || actors.Any(actor => !actor.Actor.IsCurrent || !battle.members.ContainsKey(actor.Actor) ||
                        actor.Identity.CharacterId != actor.Actor.Character || actor.Identity.InstanceId == Guid.Empty) ||
                    actors.Select(actor => actor.Actor.Character).Distinct().Count() != actors.Length ||
                    actors.Select(actor => actor.Identity.InstanceId).Distinct().Count() != 1 ||
                    battle.lukeEnergyObjects && dungeon is 5063 or 5066 &&
                        (identity.Phase != 1 || objects?.Energy is null || objects.MazeOrdinal != mazeOrdinal) ||
                    battle.dungeonRuns.Count >= (battle.Kind == RaidKind.Fiendwar ? 8 : battle.Kind == RaidKind.Luke ? 2 : 5) || battle.lukeRevivalReservations.Any(row => row.Squad == identity.Squad) ||
                    battle.dungeonRuns.Values.Any(run => run.Squad == identity.Squad) ||
                    battle.Kind == RaidKind.Luke && battle.dungeonRuns.Values.Any(run => run.Phase == identity.Phase &&
                        run.Dungeon == dungeon && run.Generation != battle.current.DungeonGenerations.GetValueOrDefault(checked((int)dungeon))) ||
                    battle.Kind == RaidKind.Luke && battle.dungeonRuns.Values.Count(run => run.Phase == identity.Phase && run.Dungeon == dungeon) >=
                        battle.script.Phases[identity.Phase].Dungeons.Single(row => row.Id == dungeon).PartyMax ||
                    actors.Any(actor => battle.dungeonRuns.Values.Any(run => run.Active.Contains(actor.Actor)))) return null;
                if (battle.UsesLukeRoute(identity.Phase)
                    ? route is null || route.Dungeon != dungeon || route.Phase != identity.Phase || route.MazeOrdinal != mazeOrdinal || !battle.IsLukeRouteCurrent(route)
                    : route is not null) return null;
                var run = new RaidDungeonRun(actors[0].Identity.InstanceId, identity.Phase, dungeon, identity.Squad,
                    battle.current.DungeonGenerations.GetValueOrDefault(checked((int)dungeon)), actors)
                    { Objects = objects, Route = route, RevivalLimit = coinLimit };
                if (!battle.ReserveFiendwarEncounter(run)) return null;
                if (!battle.dungeonRuns.TryAdd(run.Id, run)) return null;
                return new(battle, run);
            }
        }
        public void Install()
        {
            lock (battle.gate)
            {
                if (finished || !battle.dungeonRuns.TryGetValue(run.Id, out var current) || !ReferenceEquals(current, run))
                    throw new InvalidOperationException("已提交团本Run失去原入场预留");
                // 阶段结束候选在活跃/预留Run全部释放前不能替换当前phase；这里无新的业务拒绝。
                foreach (var actor in run.Actors)
                {
                    foreach (var old in battle.entries.Keys.Where(key => key.Character == actor.Actor.Character).ToArray())
                        battle.entries.Remove(old);
                    battle.entries.Add((run.Id, actor.Actor.Character), new(actor.Identity, run.Phase, run.Dungeon, run));
                }
                run.Installed = true;
                finished = true;
            }
        }
        public void Dispose()
        {
            lock (battle.gate)
            {
                if (finished) return;
                finished = true;
                if (battle.dungeonRuns.TryGetValue(run.Id, out var current) && ReferenceEquals(current, run)) { battle.dungeonRuns.Remove(run.Id); battle.fiendwarEncounters.Remove(run.Id); }
                ReleasedChange = battle.CommitPendingTransition();
            }
        }
    }

    internal DungeonReservation? ReserveDungeon(RaidDungeonIdentity identity, uint dungeon,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors, LukeRaidObjectRegistration? objects = null,
        LukeRouteLease? route = null, int? mazeOrdinal = null, int? coinLimit = null) =>
        DungeonReservation.Prepare(this, identity, dungeon, actors, objects, route, mazeOrdinal, coinLimit);

    // 各成员的正式事务全部确认后调用。先验证整批，再记录一次共享Run推进，不接受部分候选事实。
    internal AntonRaidBattleChange? CompleteLukeDungeon((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || current.PhaseExpired || LukePhaseDueLocked() || facts.Length is < 1 or > 4 ||
                !dungeonRuns.TryGetValue(facts[0].Fact.Identity.InstanceId, out var run) || !run.Installed ||
                facts.Length != run.Active.Count || run.Cleared || run.EnergyReturnEpoch != 0 || run.Phase != current.Phase ||
                current.State != AntonRaidBattleState.Fighting || facts.Select(row => row.Actor).Distinct().Count() != facts.Length) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || !ValidLukeClearSource(run, row.Actor, row.Fact) ||
                    !entries.TryGetValue((row.Fact.Identity.InstanceId, row.Actor.Character), out var entry) ||
                    !ReferenceEquals(entry.Run, run) || row.Fact.Identity.CharacterId != entry.Identity.CharacterId ||
                    row.Fact.Identity.QuestId != entry.Identity.QuestId || row.Fact.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    row.Fact.Identity.Epoch < entry.Identity.Epoch ||
                    row.Fact.Identity.Epoch < run.MapEpochs.GetValueOrDefault(row.Actor)) return null;
            var before = current;
            var candidate = pendingTransition ?? before;
            // 本服兼容合同：每个不同已提交Run扣一次图HP，不能按四名角色扣四次。
            // HP尚有剩余时图保持可重进；只有降到0才产生DGN Clear状态事件。
            if (pendingTransition is null && CurrentObjective(run))
            {
                var definition = script.Phases[run.Phase].Dungeons.Single(d => d.Id == run.Dungeon);
                if (!before.Symbols.TryGetValue(definition.HpSymbol, out int hp) || hp <= 0) return null;
                if (UsesLukeRoute(run.Phase) && (run.Route is not { } lease || !IsLukeRouteCurrent(lease) || lease.ExpectedHp != hp)) return null;
                int remaining = hp - 1;
                candidate = before with { Symbols = before.Symbols.SetItem(definition.HpSymbol, remaining),
                    Dungeons = before.Dungeons.SetItem(checked((int)run.Dungeon), remaining == 0 ? RaidDungeonState.Clear : RaidDungeonState.Open) };
                if (run.Route is { } route)
                {
                    var progress = candidate.LukeRoutes[route.Dungeon];
                    candidate = candidate with { LukeRoutes = candidate.LukeRoutes.SetItem(route.Dungeon,
                        progress with { Step = progress.Step + 1, Revision = checked(progress.Revision + 1) }) };
                }
                candidate = Evaluate(candidate, remaining == 0 ? checked((int)run.Dungeon) : null, definition.HpSymbol, null);
                if (run.EnergyRoom is { } energy)
                    candidate = candidate with { LukeEnergySignals = candidate.LukeEnergySignals.SetItem(energy.Symbol, 1) };
            }
            candidate = ReconcileLukeRoutes(candidate);
            // 第一次ClearPhase产生Reward候选即结束旧阶段业务；随后只补原Run已提交成员事实，
            // 不再扣HP、改图状态或执行旧阶段symbol/timer行为。
            // Evaluate可能拒绝未知执行语义；在它成功前不消费已提交事实，原Run可完整重放。
            RecordGuildClearParty(facts.Select(row => row.Actor.Character));
            foreach (var row in facts)
            {
                run.ClearFacts.Add(row.Actor, row.Fact);
                var key = (run.Phase, row.Actor.Character);
                int count = lukeParticipation.GetValueOrDefault(key);
                lukeParticipation[key] = count == int.MaxValue ? count : count + 1;
            }
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    // 正式安装/全员地图投影确认后调用；每个Run最多四个epoch，不记录无限房间历史。
    // 规则失败不消费事件，下次原Run加载/同步仍可重试；失效目标代仅确认事实而不执行新代业务。
    internal AntonRaidBattleChange? ObserveLukeRun(Guid instance,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors, int? map = null,
        DfoServer.GameWorld.Dungeons.DungeonMap? objectRoom = null)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || !dungeonRuns.TryGetValue(instance, out var run) || !run.Installed ||
                actors.Length == 0 || actors.Length != run.Active.Count ||
                actors.Select(row => row.Actor).Distinct().Count() != actors.Length ||
                actors.Any(row => !row.Actor.IsCurrent || !run.Active.Contains(row.Actor) ||
                    !entries.TryGetValue((instance, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                    row.Identity.InstanceId != instance || row.Identity.CharacterId != row.Actor.Character ||
                    row.Identity.QuestId != entry.Identity.QuestId || row.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    row.Identity.Epoch < entry.Identity.Epoch ||
                    row.Identity.Epoch < run.MapEpochs.GetValueOrDefault(row.Actor))) return null;
            bool enter = !run.EnterConsumed;
            bool changedMap = map is not null && actors.Any(row => row.Identity.Epoch > run.MapEpochs.GetValueOrDefault(row.Actor));
            if (!enter && !changedMap) return null;
            var before = current;
            var candidate = before;
            if (pendingTransition is null && before.State == AntonRaidBattleState.Fighting && run.EnergyReturnEpoch == 0 && CurrentObjective(run))
            {
                if (enter) candidate = Evaluate(candidate, null, null, null,
                    new(RaidStageFactKind.EnterDungeon, run.Phase, checked((int)run.Dungeon)));
                if (changedMap && candidate.State == AntonRaidBattleState.Fighting)
                    candidate = Evaluate(candidate, null, null, null,
                        new(RaidStageFactKind.EnterMap, run.Phase, checked((int)run.Dungeon), map!.Value));
            }
            run.EnterConsumed = true;
            if (map is not null) foreach (var row in actors) run.MapEpochs[row.Actor] = row.Identity.Epoch;
            if (changedMap && objectRoom is { } observedRoom && observedRoom.Id == map && pendingTransition is null && CurrentObjective(run))
            {
                var bindings = run.Objects?.Observe(observedRoom);
                run.FacilityRoom = CreateFacilityRoom(run, bindings);
                run.EnergyRoom = lukeEnergyObjects && run.Phase == 1 && bindings is [var energy] &&
                    energy.IsEnergy && energy == run.Objects?.Energy ? energy : null;
            }
            CommitBattleCandidate(before, candidate);
            return ReferenceEquals(before, current) ? null : new(before, current);
        }
    }

    // 由原阶段计时协调者传入当前实例/phase；同phase终结事实有界且只接纳一次。
    internal AntonRaidBattleChange? EndLukePhase(Guid instance, int phase)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || instance != Instance || phase != current.Phase ||
                TerminalRequested(current) || pendingTransition is not null || endedLukePhases.Contains(phase) ||
                !script.Phases[phase].Rules.Any(rule => rule.Trigger.Any(clause => clause.Tag == "[phase end]" &&
                    clause.RawIntegers.Length == 1 && clause.RawIntegers[0] == phase))) return null;
            var before = current;
            var candidate = Evaluate(before, null, null, null, new(RaidStageFactKind.PhaseEnd, phase));
            endedLukePhases.Add(phase);
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    internal bool HasDungeonContribution(RaidActor actor, Guid run)
    {
        lock (gate) return actor.IsCurrent && entries.TryGetValue((run, actor.Character), out var entry) &&
            entry.Run?.ClearFacts.ContainsKey(actor) == true;
    }

    bool CurrentObjective(RaidDungeonRun run) => !TerminalRequested(current) && !LukePhaseDueLocked() && run.Phase == current.Phase &&
        current.Dungeons.GetValueOrDefault(checked((int)run.Dungeon)) == RaidDungeonState.Open &&
        current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon)) == run.Generation;

    void CommitBattleCandidate(AntonRaidBattleView before, AntonRaidBattleView candidate)
    {
        if (candidate.LukeEnergyOverloadEpoch > before.LukeEnergyOverloadEpoch)
            foreach (var run in dungeonRuns.Values.Where(run => run.Phase == candidate.Phase &&
                run.Dungeon is 5061 or 5062 && !run.Cleared))
                run.EnergyReturnEpoch = candidate.LukeEnergyOverloadEpoch;
        if (lukeEnergyObjects)
            foreach (var (dungeon, symbol) in new[] { (5063, 113), (5066, 114) })
                if (candidate.DungeonGenerations.GetValueOrDefault(dungeon) != before.DungeonGenerations.GetValueOrDefault(dungeon))
                    candidate = candidate with { LukeEnergySignals = candidate.LukeEnergySignals.SetItem(symbol, 0) };
        if (ReferenceEquals(before, candidate)) return;
        candidate = ReconcileLukeRoutes(candidate);
        if (Kind == RaidKind.Anton && antonRewards is not null && (candidate.State == AntonRaidBattleState.Reward || candidate.PhaseExpired))
        {
            pendingTransition = candidate;
            _ = AntonPhaseSettlement.Capture(this, candidate.Phase);
            current = before with { Dungeons = candidate.Dungeons, Symbols = candidate.Symbols,
                Timers = candidate.Timers, TimerAnchors = candidate.TimerAnchors,
                TimerGenerations = candidate.TimerGenerations, Deferred = candidate.Deferred,
                DeferredAnchors = candidate.DeferredAnchors, DeferredGenerations = candidate.DeferredGenerations, Revision = before.Revision + 1 };
            ScheduleLukeClockLocked();
            return;
        }
        if (Kind is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar && candidate.State != AntonRaidBattleState.Fighting && (dungeonRuns.Count != 0 || lukeRevivalReservations.Count != 0 || lukeCompletion is not null))
        {
            pendingTransition = candidate;
            if (lukeCompletion is not null && candidate.State == AntonRaidBattleState.Reward)
                _ = LukePhaseSettlement.Capture(this, candidate.Phase);
            // 逻辑终结即停止旧阶段timer，不等旧Run全部退场；phase/Reward仍由退出屏障提交。
            if (!before.Timers.IsEmpty)
                current = before with { Timers = before.Timers.Clear(), TimerGenerations = before.TimerGenerations.Clear(), TimerAnchors = before.TimerAnchors.Clear(),
                    TimerSequence = candidate.TimerSequence, Revision = before.Revision + 1 };
            if (Kind == RaidKind.Fiendwar)
                current = candidate with { State = before.State, Revision = before.Revision + 1 };
            ScheduleLukeClockLocked();
            return;
        }
        current = candidate with { Revision = before.Revision + 1 };
        ScheduleLukeClockLocked();
    }

    AntonRaidBattleChange? CommitPendingTransition()
    {
        if (Kind == RaidKind.Anton && antonRewards is not null) return null;
        if (current.ClockFaulted || dungeonRuns.Count != 0 || lukeRevivalReservations.Count != 0 || pendingTransition is not { } pending ||
            lukeCompletion is not null && !lukePhaseCommits.ContainsKey(pending.Phase)) return null;
        var before = current;
        current = pending with { Revision = current.Revision + 1, LukePhaseCoinsUsed = current.LukePhaseCoinsUsed };
        pendingTransition = null;
        if (lukeCompletion is not null && current.State == AntonRaidBattleState.Reward &&
            lukePhaseCommits.TryGetValue(current.Phase, out var committed) && committed.Members.All(row => !row.Eligible))
            _ = AdvanceAfterRewards(Instance, current.Revision);
        ScheduleLukeClockLocked();
        return new(before, current);
    }

    internal AntonRaidBattleChange? ReleaseDungeon(RaidActor actor, Guid run)
    {
        lock (gate) return ReleaseDungeonActor(actor, run);
    }

    AntonRaidBattleChange? ReleaseDungeonActor(RaidActor actor, Guid? expectedRun)
    {
        var before = current;
        foreach (var run in dungeonRuns.Values.Where(run => (expectedRun is null || run.Id == expectedRun) && run.Active.Contains(actor)).ToArray())
        {
            // 最后成员正式退出且未清图才是GiveUp；旧目标代/已结束阶段不执行新业务。
            if (Kind == RaidKind.Luke && run.Active.Count == 1 && run.Installed && !run.Cleared && pendingTransition is null && CurrentObjective(run))
                CommitBattleCandidate(current, Evaluate(RevertLukeFacilityReports(run), null, null, null,
                    new(RaidStageFactKind.GiveUpDungeon, run.Phase, checked((int)run.Dungeon))));
            if (Kind == RaidKind.Prey && run.Active.Count == 1 && run.Installed && !run.Cleared &&
                pendingTransition is null && CurrentObjective(run) && script.Phases[run.Phase].Dungeons
                    .Any(row => row.Id == run.Dungeon && PreyDungeonType(row) == 5) &&
                Enumerable.Range(1, 16).Any(slot => current.Symbols[$"[area_{slot}]"] == run.Dungeon))
                CommitBattleCandidate(current, RemovePreyArea(DamagePreyShip(current, PreyHeader("[single defence fail]")), checked((int)run.Dungeon)));
            run.Active.Remove(actor);
            if (Kind == RaidKind.Anton) entries.Remove((run.Id, actor.Character));
            if (run.Active.Count == 0)
            {
                if (fiendwarEncounters.TryGetValue(run.Id, out var encounter) && current.FiendwarDungeonTimers.ContainsKey(encounter.Area))
                    current = current with { FiendwarDungeonTimers = current.FiendwarDungeonTimers.Remove(encounter.Area), Revision = current.Revision + 1 };
                dungeonRuns.Remove(run.Id); fiendwarEncounters.Remove(run.Id); fiendwarBossReports.Remove(run.Id);
                if (current.FiendwarExpiredRuns.Contains(run.Id))
                    current = current with { FiendwarExpiredRuns = current.FiendwarExpiredRuns.Remove(run.Id), Revision = current.Revision + 1 };
            }
        }
        var transition = CommitPendingTransition();
        return transition is not null || !ReferenceEquals(before, current) ? new(before, current) : null;
    }
}
