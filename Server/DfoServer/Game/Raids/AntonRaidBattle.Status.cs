using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed record RaidRunDisplay(Guid Run, uint Dungeon, byte Squad, ImmutableArray<ushort> Members,
    uint UsedCoins, uint Context, ImmutableArray<uint> Values, bool Cleared);

internal sealed partial class AntonRaidBattle
{
    internal RaidRunDisplay[] StatusSnapshot(AntonRaidBattleView view)
    {
        lock (gate) return !ReferenceEquals(current, view) || view.PhaseExpired ? [] : dungeonRuns.Values
            .Where(run => run.Installed && run.Phase == view.Phase && run.Active.Any(actor => actor.IsCurrent))
            .OrderBy(run => run.Dungeon).ThenBy(run => run.Squad).Select(run => new RaidRunDisplay(run.Id, run.Dungeon,
                run.Squad, [.. run.Active.Where(actor => actor.IsCurrent).OrderBy(actor => actor.Character).Select(actor => actor.Character)],
                Kind == RaidKind.Luke ? checked((uint)run.RevivalUsed) : run.Hp?.UsedCoins ?? checked((uint)run.RevivalUsed),
                run.Hp?.Context ?? 0, run.Hp?.Values ?? [], run.Cleared)).ToArray();
    }

    internal AntonRaidBattleChange? ReportStatusHp(RaidActor actor, DungeonRunIdentity identity, AntonHpDisplay display)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor) || pendingTransition is not null || TerminalRequested(current) ||
                !dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed || run.Phase != current.Phase ||
                !run.Active.Contains(actor) || run.Squad != display.Squad || display.Members.Length != run.Active.Count ||
                run.Active.Any(member => !member.IsCurrent || !display.Members.Contains(member.Character)) ||
                !entries.TryGetValue((run.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                identity.Epoch < entry.Identity.Epoch || identity.QuestId != entry.Identity.QuestId || identity.TaskAttempt != entry.Identity.TaskAttempt)
                return null;
            run.Hp = display;
            var before = current;
            current = current with { Revision = checked(current.Revision + 1) };
            return new(before, current);
        }
    }
}
