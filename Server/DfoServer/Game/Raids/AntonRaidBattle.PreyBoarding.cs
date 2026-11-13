namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 成队协调归原攻坚实例；不建立第二套成员或待机目录。
    internal SemaphoreSlim PreyBoardingCommands { get; } = new(1, 1);
    long preyBoardingSequence;
    internal long NextPreyBoardingOrder() => Interlocked.Increment(ref preyBoardingSequence);

    readonly Dictionary<RaidActor, PreySideEntry> preySideEntries = [];

    // 名额覆盖位置SQL之前的在途入侧；退出者须等原位置提交后才释放旧侧名额。
    internal PreySideEntry? ReservePreySideEntry(RaidActor actor, byte destination)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || destination is not (4 or 5) ||
                !actor.IsCurrent || !members.ContainsKey(actor) || preySideEntries.ContainsKey(actor) ||
                current.State != AntonRaidBattleState.Fighting || current.PhaseExpired || current.ClockFaulted ||
                pendingTransition is not null || TerminalRequested(current)) return null;
            int occupied = members.Keys.Count(member => member != actor && member.IsCurrent &&
                (member.Session.Selected?.Position is { Town: 59 } position && position.Area == destination &&
                    !dungeonRuns.Values.Any(run => run.Installed && run.Active.Contains(member)) ||
                 preySideEntries.GetValueOrDefault(member)?.Destination == destination));
            if (occupied >= 4) return null;
            var entry = new PreySideEntry(this, actor, destination);
            preySideEntries.Add(actor, entry);
            return entry;
        }
    }

    internal sealed class PreySideEntry(AntonRaidBattle battle, RaidActor actor, byte destination) : IDisposable
    {
        internal byte Destination { get; } = destination;
        public void Dispose()
        {
            lock (battle.gate)
                if (ReferenceEquals(battle.preySideEntries.GetValueOrDefault(actor), this))
                    battle.preySideEntries.Remove(actor);
        }
    }
}
