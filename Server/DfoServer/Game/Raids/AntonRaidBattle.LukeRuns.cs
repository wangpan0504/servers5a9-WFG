using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 预留与活跃Run共用两分队上限。已退出Run仅由至多八条成员绑定引用，不保留无限游玩历史。
    sealed class LukeDungeonRun(Guid id, int phase, uint dungeon, byte squad,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors)
    {
        public Guid Id { get; } = id;
        public int Phase { get; } = phase;
        public uint Dungeon { get; } = dungeon;
        public byte Squad { get; } = squad;
        public (RaidActor Actor, DungeonRunIdentity Identity)[] Actors { get; } = actors;
        public HashSet<RaidActor> Active { get; } = actors.Select(actor => actor.Actor).ToHashSet();
        public bool Installed { get; set; }
        public Dictionary<RaidActor, DungeonClearFact> ClearFacts { get; } = [];
        public bool Cleared => ClearFacts.Count != 0;
    }
    readonly Dictionary<Guid, LukeDungeonRun> lukeRuns = [];
    AntonRaidBattleView? pendingTransition;
    public bool HasPendingTransition { get { lock (gate) return pendingTransition is not null; } }

    // 能力对象仅由原Battle签发；Dispose只撤销尚未安装的本次预留，不释放成功安装后的Run。
    internal sealed class DungeonReservation : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly LukeDungeonRun run;
        bool finished;
        private DungeonReservation(AntonRaidBattle battle, LukeDungeonRun run) { this.battle = battle; this.run = run; }
        internal static DungeonReservation? Prepare(AntonRaidBattle battle, RaidDungeonIdentity identity,
            uint dungeon, (RaidActor Actor, DungeonRunIdentity Identity)[] actors)
        {
            lock (battle.gate)
            {
                if (battle.Kind != RaidKind.Luke || identity.Kind != RaidKind.Luke || identity.InstanceId != battle.Instance ||
                    identity.Phase != battle.current.Phase || identity.Squad is not (1 or 2) ||
                    battle.current.State != AntonRaidBattleState.Fighting || battle.pendingTransition is not null ||
                    !battle.current.Dungeons.TryGetValue(checked((int)dungeon), out var state) || state != RaidDungeonState.Open ||
                    actors.Length is < 1 or > 4 || actors.Any(actor => !actor.Actor.IsCurrent || !battle.members.ContainsKey(actor.Actor) ||
                        actor.Identity.CharacterId != actor.Actor.Character || actor.Identity.InstanceId == Guid.Empty) ||
                    actors.Select(actor => actor.Actor.Character).Distinct().Count() != actors.Length ||
                    actors.Select(actor => actor.Identity.InstanceId).Distinct().Count() != 1 ||
                    battle.lukeRuns.Count >= 2 || battle.lukeRuns.Values.Any(run => run.Squad == identity.Squad ||
                        run.Phase == identity.Phase && run.Dungeon == dungeon) ||
                    actors.Any(actor => battle.lukeRuns.Values.Any(run => run.Active.Contains(actor.Actor)))) return null;
                var run = new LukeDungeonRun(actors[0].Identity.InstanceId, identity.Phase, dungeon, identity.Squad, actors);
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
                battle.CommitPendingTransition();
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
                    row.Fact.Identity.Epoch < entry.Identity.Epoch) return null;
            var before = current;
            var basis = pendingTransition ?? before;
            var candidate = basis with { Dungeons = basis.Dungeons.SetItem(checked((int)run.Dungeon), RaidDungeonState.Clear) };
            // 第一次ClearPhase产生Reward候选即结束旧阶段业务；延迟的只是Run退出后的可见转换。
            // 此后仅接纳旧Run已经提交的成员事实，不再次执行已逻辑结束阶段的symbol/timer行为。
            if (pendingTransition is null) candidate = Evaluate(candidate, checked((int)run.Dungeon), null, null);
            // Evaluate可能拒绝未知执行语义；在它成功前不消费已提交事实，原Run可完整重放。
            foreach (var row in facts) run.ClearFacts.Add(row.Actor, row.Fact);
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    internal bool HasDungeonContribution(RaidActor actor, Guid run)
    {
        lock (gate) return actor.IsCurrent && entries.TryGetValue((run, actor.Character), out var entry) &&
            entry.Luke?.ClearFacts.ContainsKey(actor) == true;
    }

    void CommitBattleCandidate(AntonRaidBattleView before, AntonRaidBattleView candidate)
    {
        if (Kind == RaidKind.Luke && candidate.State != AntonRaidBattleState.Fighting && lukeRuns.Count != 0)
        {
            pendingTransition = candidate;
            return;
        }
        current = candidate with { Revision = before.Revision + 1 };
    }

    AntonRaidBattleChange? CommitPendingTransition()
    {
        if (lukeRuns.Count != 0 || pendingTransition is not { } pending) return null;
        var before = current;
        current = pending with { Revision = current.Revision + 1 };
        pendingTransition = null;
        return new(before, current);
    }

    internal AntonRaidBattleChange? ReleaseDungeon(RaidActor actor, Guid run)
    {
        lock (gate) return ReleaseLukeActor(actor, run);
    }

    AntonRaidBattleChange? ReleaseLukeActor(RaidActor actor, Guid? expectedRun)
    {
        foreach (var run in lukeRuns.Values.Where(run => (expectedRun is null || run.Id == expectedRun) && run.Active.Contains(actor)).ToArray())
        {
            run.Active.Remove(actor);
            if (run.Active.Count == 0) lukeRuns.Remove(run.Id);
        }
        return CommitPendingTransition();
    }
}
