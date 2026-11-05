using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    bool HasPreyDungeonDeadline(RaidDungeonRun run) => Kind == RaidKind.Prey && !PreyGuide && run.Installed &&
        !run.Cleared && run.Active.Count != 0 && run.PreyTimeLimitSeconds is > 0 && run.PreyTimeLimitAnchor is not null &&
        CurrentObjective(run) && !current.PreyExpelledRuns.Contains(run.Id);

    TimeSpan PreyDungeonRemaining(RaidDungeonRun run) =>
        TimeSpan.FromSeconds(run.PreyTimeLimitSeconds!.Value) - lukeClock.GetElapsedTime(run.PreyTimeLimitAnchor!.Value);

    bool PreyDungeonTimeDueLocked(RaidDungeonRun run) => HasPreyDungeonDeadline(run) && PreyDungeonRemaining(run) <= TimeSpan.Zero;

    internal uint? PreyDungeonSeconds(RaidActor actor, DungeonRunIdentity identity)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor) || pendingTransition is not null ||
                !dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Active.Contains(actor) ||
                !HasPreyDungeonDeadline(run) || !entries.TryGetValue((run.Id, actor.Character), out var entry) ||
                !ReferenceEquals(entry.Run, run) || identity.CharacterId != actor.Character ||
                identity.QuestId != entry.Identity.QuestId || identity.TaskAttempt != entry.Identity.TaskAttempt ||
                identity.Epoch < entry.Identity.Epoch) return null;
            var remaining = PreyDungeonRemaining(run);
            return remaining > TimeSpan.Zero ? checked((uint)Math.Ceiling(remaining.TotalSeconds)) : null;
        }
    }

    TimeSpan? PreyDungeonRemainingLocked()
    {
        TimeSpan? earliest = null;
        foreach (var run in dungeonRuns.Values)
        {
            if (!HasPreyDungeonDeadline(run)) continue;
            var remaining = PreyDungeonRemaining(run);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (earliest is null || remaining < earliest) earliest = remaining;
        }
        return earliest;
    }

    void ExpireDuePreyDungeonsLocked()
    {
        // 期限绑定正式Run而非分队或DGN；旧回调不能退出后续挑战，也不能当作阶段通关。
        foreach (var run in dungeonRuns.Values.Where(PreyDungeonTimeDueLocked).ToArray())
        {
            if (pendingTransition is not null || TerminalRequested(current) || !PreyDungeonTimeDueLocked(run)) continue;
            var before = current;
            var expired = before with { PreyExpelledRuns = before.PreyExpelledRuns.Add(run.Id) };
            CommitBattleCandidate(before, GiveUpPreyTeamDungeon(expired, checked((int)run.Dungeon)));
        }
    }
}
