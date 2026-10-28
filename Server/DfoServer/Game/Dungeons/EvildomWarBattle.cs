using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal sealed record EvildomWarView(Guid BattleId, long Revision, byte Mode, DateTimeOffset Deadline,
    ImmutableArray<EvildomWarDungeonState> Dungeons)
{
    public bool Completed => Dungeons.Any(row => row.Kind == 1 && row.State == 2);
    public bool SealsCleared => Dungeons.Count(row => row.Kind == 0 && row.Attribute < 2 && row.State == 2) == 2;
    public EvildomWarHiddenEncounter? Hidden { get; init; }
    public uint ActualDungeon(uint stage) => Hidden is { } hidden && hidden.StageDungeon == stage ? hidden.Dungeon : stage;
    public uint StageDungeon(uint actual) => Hidden is { } hidden && hidden.Dungeon == actual ? hidden.StageDungeon : actual;
}

internal sealed record EvildomWarCompletion(Guid BattleId, byte Mode, DateTimeOffset Created,
    ImmutableArray<DungeonClearFact> Facts);

// 战役跨多个正式Run；每名成员的身份与费用仍由原Session和全员入场事务负责。
internal sealed class EvildomWarBattle(byte mode, DateTimeOffset deadline, ImmutableArray<EvildomWarDungeonState> dungeons,
    EvildomWarHiddenEncounter? hidden = null)
{
    readonly object gate = new();
    EvildomWarView view = new(Guid.NewGuid(), 0, mode, deadline, dungeons) { Hidden = hidden };
    uint activeDungeon;
    DungeonRunIdentity[] activeRuns = [];
    readonly HashSet<ushort> released = [];
    Reservation? pending;
    EvildomWarCompletion? completion;
    EvildomWarCompletion? hiddenCompletion;

    internal sealed class Reservation(EvildomWarBattle battle, uint dungeon, DungeonRunIdentity[] runs) : IDisposable
    {
        public EvildomWarView Install()
        {
            lock (battle.gate)
            {
                if (!ReferenceEquals(battle.pending, this)) throw new InvalidOperationException("魔界Run预留已失效");
                battle.activeDungeon = dungeon;
                battle.activeRuns = runs;
                battle.released.Clear();
                battle.pending = null;
                return battle.Change(row => row.Id == dungeon ? row with { State = 1 } : row);
            }
        }

        public void Dispose()
        {
            lock (battle.gate)
                if (ReferenceEquals(battle.pending, this)) battle.pending = null;
        }
    }

    public EvildomWarView View { get { lock (gate) return view; } }
    public EvildomWarCompletion? Completion { get { lock (gate) return completion; } }
    public EvildomWarCompletion? HiddenCompletion { get { lock (gate) return hiddenCompletion; } }

    public static EvildomWarBattle Restore(EvildomWarCheckpoint checkpoint)
    {
        var restored = new EvildomWarBattle(checkpoint.View.Mode, checkpoint.View.Deadline, checkpoint.View.Dungeons);
        restored.view = checkpoint.View;
        restored.completion = checkpoint.Completion;
        restored.hiddenCompletion = checkpoint.HiddenCompletion;
        return restored;
    }

    public void RecoverCheckpoint(Action<EvildomWarView, EvildomWarCheckpoint> persist)
    {
        lock (gate)
        {
            bool reset = activeRuns.Length == released.Count && view.Dungeons.Any(row => row.State == 1);
            bool unlock = view.SealsCleared && view.Dungeons.Any(row => row.Kind == 1 && row.State == 3);
            if (!reset && !unlock) return;
            var after = view with { Revision = view.Revision + 1,
                Dungeons = view.Dungeons.Select(row => reset && row.State == 1 || unlock && row.Kind == 1 && row.State == 3
                    ? row with { State = 0 } : row).ToImmutableArray() };
            persist(view, new(after, completion) { HiddenCompletion = hiddenCompletion });
            if (reset) { activeRuns = []; released.Clear(); }
            view = after;
        }
    }

    public EvildomWarView? Enter(uint dungeon, DungeonRunIdentity[] runs, DateTimeOffset now)
    {
        using var reservation = Reserve(dungeon, runs, now);
        return reservation?.Install();
    }

    public Reservation? Reserve(uint dungeon, DungeonRunIdentity[] runs, DateTimeOffset now)
    {
        lock (gate)
        {
            dungeon = view.StageDungeon(dungeon);
            if (now >= view.Deadline || view.Completed || pending is not null || activeRuns.Length != 0 || runs.Length is < 1 or > 4 ||
                runs.Any(run => run.InstanceId == Guid.Empty || run.InstanceId != runs[0].InstanceId) ||
                runs.Select(run => run.CharacterId).Distinct().Count() != runs.Length ||
                !view.Dungeons.Any(row => row.Id == dungeon && row.State == 0)) return null;
            return pending = new(this, dungeon, [..runs]);
        }
    }

    public EvildomWarView? Complete(DungeonClearFact[] facts, DateTimeOffset now,
        Action<EvildomWarView, EvildomWarCheckpoint>? persist = null)
    {
        lock (gate)
        {
            if (now >= view.Deadline || activeRuns.Length == 0 || facts.Length == 0 || facts.Length != activeRuns.Length - released.Count ||
                facts.Select(fact => fact.Identity.CharacterId).Distinct().Count() != facts.Length ||
                facts.Any(fact => released.Contains(fact.Identity.CharacterId) || fact.DungeonId != view.ActualDungeon(activeDungeon) ||
                    !activeRuns.Any(run => SameRun(run, fact.Identity)))) return null;
            uint completedDungeon = activeDungeon;
            var completed = view.Dungeons.Select(row => row.Id == completedDungeon ? row with { State = 2 } : row).ToImmutableArray();
            bool unlock = completed.Count(row => row.Kind == 0 && row.Attribute < 2 && row.State == 2) == 2;
            var after = view with { Revision = view.Revision + 1, Dungeons = completed.Select(row =>
                unlock && row.Kind == 1 && row.State == 3 ? row with { State = 0 } : row).ToImmutableArray() };
            var finished = after.Completed ? new EvildomWarCompletion(view.BattleId, view.Mode, now, facts.Select(fact => fact with
                    { ConfirmedDeathEvents = fact.ConfirmedDeathEvents.ToImmutableArray() }).ToImmutableArray()) : null;
            var laboratory = view.Hidden?.StageDungeon == completedDungeon
                ? new EvildomWarCompletion(view.BattleId, view.Mode, now, facts.Select(fact => fact with
                    { ConfirmedDeathEvents = fact.ConfirmedDeathEvents.ToImmutableArray() }).ToImmutableArray()) : hiddenCompletion;
            persist?.Invoke(view, new(after, finished) { HiddenCompletion = laboratory });
            activeRuns = [];
            released.Clear();
            view = after;
            completion = finished;
            hiddenCompletion = laboratory;
            return view;
        }
    }

    public EvildomWarView? Release(DungeonRunIdentity run, Action<EvildomWarView, EvildomWarCheckpoint>? persist = null, bool defer = false)
    {
        lock (gate)
        {
            if (!activeRuns.Any(current => SameRun(current, run))) return null;
            released.Add(run.CharacterId);
            if (released.Count != activeRuns.Length || defer) return null;
            var after = view with { Revision = view.Revision + 1, Dungeons = view.Dungeons.Select(row =>
                row.Id == activeDungeon && row.State == 1 ? row with { State = 0 } : row).ToImmutableArray() };
            persist?.Invoke(view, new(after, completion) { HiddenCompletion = hiddenCompletion });
            activeRuns = [];
            released.Clear();
            return view = after;
        }
    }

    EvildomWarView Change(Func<EvildomWarDungeonState, EvildomWarDungeonState> update) =>
        view = view with { Revision = view.Revision + 1, Dungeons = view.Dungeons.Select(update).ToImmutableArray() };

    // Run.Epoch随过房及通关演进；成员Session另行复验当前完整identity和已确认事实。
    static bool SameRun(DungeonRunIdentity first, DungeonRunIdentity second) => first.CharacterId == second.CharacterId &&
        first.QuestId == second.QuestId && first.TaskAttempt == second.TaskAttempt && first.InstanceId == second.InstanceId;
}
