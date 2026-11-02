using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed record RaidRunDisplay(Guid Run, uint Dungeon, byte Squad, ImmutableArray<ushort> Members,
    uint UsedCoins, uint Context, ImmutableArray<uint> Values, bool Cleared)
{
    public ImmutableArray<ushort> ClearedMembers { get; init; } = [];
}

internal sealed partial class AntonRaidBattle
{
    internal RaidRunDisplay[] StatusSnapshot(AntonRaidBattleView view)
    {
        lock (gate) return !ReferenceEquals(current, view) || view.PhaseExpired ? [] : dungeonRuns.Values
            .Where(run => run.Installed && run.Phase == view.Phase && run.Active.Any(actor => actor.IsCurrent))
            .OrderBy(run => run.Dungeon).ThenBy(run => run.Squad).Select(run => new RaidRunDisplay(run.Id, run.Dungeon,
                run.Squad, [.. run.Active.Where(actor => actor.IsCurrent).OrderBy(actor => actor.Character).Select(actor => actor.Character)],
                Kind == RaidKind.Luke ? checked((uint)run.RevivalUsed) : run.Hp?.UsedCoins ?? checked((uint)run.RevivalUsed),
                run.Hp?.Context ?? 0,
                // 原序百分比按真实监控死亡与全员清图事实投影，未知目录不猜目标形状。
                RunHpValues(run),
                run.Cleared) { ClearedMembers = run.ClearedMembers }).ToArray();
    }

    internal AntonRaidBattleChange? ReportStatusHp(RaidActor actor, DungeonRunIdentity identity, AntonHpDisplay display)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor) || pendingTransition is not null || TerminalRequested(current) ||
                !dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed || run.Phase != current.Phase ||
                Kind == RaidKind.Luke && run.Cleared ||
                !run.Active.Contains(actor) || run.Squad != display.Squad || display.Members.Length != run.Active.Count ||
                run.Active.Any(member => !member.IsCurrent || !display.Members.Contains(member.Character)) ||
                !entries.TryGetValue((run.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                identity.Epoch < entry.Identity.Epoch || identity.QuestId != entry.Identity.QuestId || identity.TaskAttempt != entry.Identity.TaskAttempt)
                return null;
            var targets = LukeMonitorTargets(run);
            if (!targets.IsEmpty && display.Values.Length != targets.Length) return null;
            run.Hp = display;
            var before = current;
            current = current with { Revision = checked(current.Revision + 1) };
            return new(before, current);
        }
    }
}
