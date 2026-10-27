using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 预留与活跃Run共用两分队上限。已退出Run仅由至多八条成员绑定引用，不保留无限游玩历史。
    sealed class LukeDungeonRun(Guid id, int phase, uint dungeon, byte squad, long generation,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors)
    {
        public Guid Id { get; } = id;
        public int Phase { get; } = phase;
        public uint Dungeon { get; } = dungeon;
        public byte Squad { get; } = squad;
        public long Generation { get; } = generation;
        public (RaidActor Actor, DungeonRunIdentity Identity)[] Actors { get; } = actors;
        public HashSet<RaidActor> Active { get; } = actors.Select(actor => actor.Actor).ToHashSet();
        public int RevivalUsed { get; set; }
        public Dictionary<RaidActor, long> RevivedDeaths { get; } = [];
        public bool Installed { get; set; }
        public bool EnterConsumed { get; set; }
        public Dictionary<RaidActor, long> MapEpochs { get; } = [];
        public Dictionary<RaidActor, DungeonClearFact> ClearFacts { get; } = [];
        public bool Cleared => ClearFacts.Count != 0;
    }
    readonly Dictionary<Guid, LukeDungeonRun> lukeRuns = [];
    AntonRaidBattleView? pendingTransition;
    readonly HashSet<int> endedLukePhases = [];
    public bool HasPendingTransition { get { lock (gate) return pendingTransition is not null; } }

    // 能力对象仅由原Battle签发；Dispose只撤销尚未安装的本次预留，不释放成功安装后的Run。
    internal sealed class DungeonReservation : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly LukeDungeonRun run;
        bool finished;
        internal AntonRaidBattleChange? ReleasedChange { get; private set; }
        private DungeonReservation(AntonRaidBattle battle, LukeDungeonRun run) { this.battle = battle; this.run = run; }
        internal static DungeonReservation? Prepare(AntonRaidBattle battle, RaidDungeonIdentity identity,
            uint dungeon, (RaidActor Actor, DungeonRunIdentity Identity)[] actors)
        {
            lock (battle.gate)
            {
                if (battle.Kind != RaidKind.Luke || identity.Kind != RaidKind.Luke || identity.InstanceId != battle.Instance ||
                    identity.Phase != battle.current.Phase || identity.Squad is not (1 or 2) ||
                    TerminalRequested(battle.current) || battle.pendingTransition is not null ||
                    !battle.current.Dungeons.TryGetValue(checked((int)dungeon), out var state) || state != RaidDungeonState.Open ||
                    actors.Length is < 1 or > 4 || actors.Any(actor => !actor.Actor.IsCurrent || !battle.members.ContainsKey(actor.Actor) ||
                        actor.Identity.CharacterId != actor.Actor.Character || actor.Identity.InstanceId == Guid.Empty) ||
                    actors.Select(actor => actor.Actor.Character).Distinct().Count() != actors.Length ||
                    actors.Select(actor => actor.Identity.InstanceId).Distinct().Count() != 1 ||
                    battle.lukeRuns.Count >= 2 || battle.lukeRevivalReservations.Any(row => row.Squad == identity.Squad) || battle.lukeRuns.Values.Any(run => run.Squad == identity.Squad ||
                        run.Phase == identity.Phase && run.Dungeon == dungeon) ||
                    actors.Any(actor => battle.lukeRuns.Values.Any(run => run.Active.Contains(actor.Actor)))) return null;
                var run = new LukeDungeonRun(actors[0].Identity.InstanceId, identity.Phase, dungeon, identity.Squad,
                    battle.current.DungeonGenerations.GetValueOrDefault(checked((int)dungeon)), actors);
                if (!battle.lukeRuns.TryAdd(run.Id, run)) return null;
                return new(battle, run);
            }
        }
        public void Install()
        {
            lock (battle.gate)
            {
                if (finished || !battle.lukeRuns.TryGetValue(run.Id, out var current) || !ReferenceEquals(current, run))
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
                if (battle.lukeRuns.TryGetValue(run.Id, out var current) && ReferenceEquals(current, run)) battle.lukeRuns.Remove(run.Id);
                ReleasedChange = battle.CommitPendingTransition();
            }
        }
    }

    internal DungeonReservation? ReserveDungeon(RaidDungeonIdentity identity, uint dungeon,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors) => DungeonReservation.Prepare(this, identity, dungeon, actors);

    // 各成员的正式事务全部确认后调用。先验证整批，再记录一次共享Run推进，不接受部分候选事实。
    internal AntonRaidBattleChange? CompleteLukeDungeon((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || facts.Length is < 1 or > 4 ||
                !lukeRuns.TryGetValue(facts[0].Fact.Identity.InstanceId, out var run) || !run.Installed ||
                facts.Length != run.Active.Count || run.Cleared || run.Phase != current.Phase ||
                current.State != AntonRaidBattleState.Fighting || facts.Select(row => row.Actor).Distinct().Count() != facts.Length) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                    !entries.TryGetValue((row.Fact.Identity.InstanceId, row.Actor.Character), out var entry) ||
                    !ReferenceEquals(entry.Luke, run) || row.Fact.Identity.CharacterId != entry.Identity.CharacterId ||
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
                int remaining = hp - 1;
                candidate = before with { Symbols = before.Symbols.SetItem(definition.HpSymbol, remaining),
                    Dungeons = before.Dungeons.SetItem(checked((int)run.Dungeon), remaining == 0 ? RaidDungeonState.Clear : RaidDungeonState.Open) };
                candidate = Evaluate(candidate, remaining == 0 ? checked((int)run.Dungeon) : null, definition.HpSymbol, null);
            }
            // 第一次ClearPhase产生Reward候选即结束旧阶段业务；随后只补原Run已提交成员事实，
            // 不再扣HP、改图状态或执行旧阶段symbol/timer行为。
            // Evaluate可能拒绝未知执行语义；在它成功前不消费已提交事实，原Run可完整重放。
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
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors, int? map = null)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || !lukeRuns.TryGetValue(instance, out var run) || !run.Installed ||
                actors.Length == 0 || actors.Length != run.Active.Count ||
                actors.Select(row => row.Actor).Distinct().Count() != actors.Length ||
                actors.Any(row => !row.Actor.IsCurrent || !run.Active.Contains(row.Actor) ||
                    !entries.TryGetValue((instance, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Luke, run) ||
                    row.Identity.InstanceId != instance || row.Identity.CharacterId != row.Actor.Character ||
                    row.Identity.QuestId != entry.Identity.QuestId || row.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    row.Identity.Epoch < entry.Identity.Epoch ||
                    row.Identity.Epoch < run.MapEpochs.GetValueOrDefault(row.Actor))) return null;
            bool enter = !run.EnterConsumed;
            bool changedMap = map is not null && actors.Any(row => row.Identity.Epoch > run.MapEpochs.GetValueOrDefault(row.Actor));
            if (!enter && !changedMap) return null;
            var before = current;
            var candidate = before;
            if (pendingTransition is null && before.State == AntonRaidBattleState.Fighting && CurrentObjective(run))
            {
                if (enter) candidate = Evaluate(candidate, null, null, null,
                    new(RaidStageFactKind.EnterDungeon, run.Phase, checked((int)run.Dungeon)));
                if (changedMap && candidate.State == AntonRaidBattleState.Fighting)
                    candidate = Evaluate(candidate, null, null, null,
                        new(RaidStageFactKind.EnterMap, run.Phase, checked((int)run.Dungeon), map!.Value));
            }
            run.EnterConsumed = true;
            if (map is not null) foreach (var row in actors) run.MapEpochs[row.Actor] = row.Identity.Epoch;
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
            entry.Luke?.ClearFacts.ContainsKey(actor) == true;
    }

    bool CurrentObjective(LukeDungeonRun run) => !TerminalRequested(current) && run.Phase == current.Phase &&
        current.Dungeons.GetValueOrDefault(checked((int)run.Dungeon)) == RaidDungeonState.Open &&
        current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon)) == run.Generation;

    void CommitBattleCandidate(AntonRaidBattleView before, AntonRaidBattleView candidate)
    {
        if (ReferenceEquals(before, candidate)) return;
        if (Kind == RaidKind.Luke && candidate.State != AntonRaidBattleState.Fighting && (lukeRuns.Count != 0 || lukeRevivalReservations.Count != 0 || lukeCompletion is not null))
        {
            pendingTransition = candidate;
            if (lukeCompletion is not null && candidate.State == AntonRaidBattleState.Reward)
                _ = LukePhaseSettlement.Capture(this, candidate.Phase);
            // 逻辑终结即停止旧阶段timer，不等旧Run全部退场；phase/Reward仍由退出屏障提交。
            if (!before.Timers.IsEmpty)
                current = before with { Timers = before.Timers.Clear(), TimerGenerations = before.TimerGenerations.Clear(),
                    TimerSequence = candidate.TimerSequence, Revision = before.Revision + 1 };
            return;
        }
        current = candidate with { Revision = before.Revision + 1 };
    }

    AntonRaidBattleChange? CommitPendingTransition()
    {
        if (lukeRuns.Count != 0 || lukeRevivalReservations.Count != 0 || pendingTransition is not { } pending ||
            lukeCompletion is not null && !lukePhaseCommits.ContainsKey(pending.Phase)) return null;
        var before = current;
        current = pending with { Revision = current.Revision + 1, LukePhaseCoinsUsed = current.LukePhaseCoinsUsed };
        pendingTransition = null;
        if (lukeCompletion is not null && current.State == AntonRaidBattleState.Reward &&
            lukePhaseCommits.TryGetValue(current.Phase, out var committed) && committed.Members.All(row => !row.Eligible))
            _ = AdvanceAfterRewards(Instance, current.Revision);
        return new(before, current);
    }

    internal AntonRaidBattleChange? ReleaseDungeon(RaidActor actor, Guid run)
    {
        lock (gate) return ReleaseLukeActor(actor, run);
    }

    AntonRaidBattleChange? ReleaseLukeActor(RaidActor actor, Guid? expectedRun)
    {
        var before = current;
        foreach (var run in lukeRuns.Values.Where(run => (expectedRun is null || run.Id == expectedRun) && run.Active.Contains(actor)).ToArray())
        {
            // 最后成员正式退出且未清图才是GiveUp；旧目标代/已结束阶段不执行新业务。
            if (run.Active.Count == 1 && run.Installed && !run.Cleared && pendingTransition is null && CurrentObjective(run))
                CommitBattleCandidate(current, Evaluate(current, null, null, null,
                    new(RaidStageFactKind.GiveUpDungeon, run.Phase, checked((int)run.Dungeon))));
            run.Active.Remove(actor);
            if (run.Active.Count == 0) lukeRuns.Remove(run.Id);
        }
        var transition = CommitPendingTransition();
        return transition is not null || !ReferenceEquals(before, current) ? new(before, current) : null;
    }
}
