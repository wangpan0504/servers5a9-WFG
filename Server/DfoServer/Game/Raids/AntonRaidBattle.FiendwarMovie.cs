namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<ushort, byte> fiendwarRewardCards = [];

    // 只给正式完成事务已密封Present集合的漩涡创建演出义务；无资格不是失败或空奖励。
    bool HasFiendwarMovieBarrierLocked() => Kind == RaidKind.Fiendwar && lukeCompletion is not null &&
        lukeSettlements.ContainsKey(current.Phase) && lukePhaseCommits.ContainsKey(current.Phase);

    internal bool FiendwarRewardExpired
    { get { lock (gate) return Kind == RaidKind.Fiendwar && LukeRewardRemainingLocked() == TimeSpan.Zero; } }

    internal byte AutomaticFiendwarCard(ushort character, byte frozen)
    { lock (gate) return Kind == RaidKind.Fiendwar ? fiendwarRewardCards.GetValueOrDefault(character, frozen) : frozen; }

    internal bool CanConfirmFiendwarMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasFiendwarMovieBarrierLocked() && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired &&
            lukeSettlements[view.Phase].Members.Any(member => member.Character == actor.Character && member.Present) &&
            !lukeMovieDeparted.Contains((view.Phase, actor.Character));
    }

    internal bool CompleteFiendwarMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!CanConfirmFiendwarMovie(actor, view) || !lukeMovieCompleted.Add((view.Phase, actor.Character))) return false;
            if (LukeMovieBarrierReadyLocked()) _ = AdvanceAfterRewards(Instance, current.Revision);
            return true;
        }
    }

    internal bool FiendwarMovieCardsReady(AntonRaidBattleView view)
    {
        lock (gate) return HasFiendwarMovieBarrierLocked() && ReferenceEquals(current, view) &&
            view.State == AntonRaidBattleState.Reward && !view.ClockFaulted && !view.PhaseExpired && LukeMovieBarrierReadyLocked();
    }
}
