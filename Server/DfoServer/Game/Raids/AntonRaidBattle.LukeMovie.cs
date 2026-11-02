namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 仅P1终结候选的Present成员参与；最多八人，随本Battle销毁。
    readonly HashSet<ushort> lukeFirstMovieCompleted = [];
    readonly HashSet<ushort> lukeFirstMovieDeparted = [];

    // 完整卢克流程且已有密封Present集合才具有演出屏障；领域诊断不创建不可能完成的UI义务。
    bool HasLukeFirstMovieLocked() => Kind == RaidKind.Luke && lukeFlow is not null && lukeSettlements.ContainsKey(0);

    bool LukeFirstMovieReadyLocked() => lukeSettlements.TryGetValue(0, out var settlement) &&
        settlement.Members.Where(member => member.Present).All(member =>
            lukeFirstMovieCompleted.Contains(member.Character) || lukeFirstMovieDeparted.Contains(member.Character));

    internal bool CanConfirmLukeMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return Kind == RaidKind.Luke && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired &&
            (view.Phase == 0
                ? lukeSettlements.TryGetValue(0, out var settlement) && settlement.Members.Any(member =>
                    member.Character == actor.Character && member.Present) && !lukeFirstMovieDeparted.Contains(actor.Character)
                : LukeEligibility(actor.Character, view.Phase) is { Eligible: true });
    }

    // 只有真实659=1调用；0仅请求跳过，不在此记账。发布由调用方在所有owner/Battle锁外完成。
    internal bool CompleteLukeFirstMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (view.Phase != 0 || !CanConfirmLukeMovie(actor, view)) return false;
            if (!lukeFirstMovieCompleted.Add(actor.Character)) return false;
            // 无领奖资格也须完成成功演出；全员确认后沿原奖励屏障推进，不重造资格或发空奖。
            if (LukeFirstMovieReadyLocked()) _ = AdvanceAfterRewards(Instance, current.Revision);
            return true;
        }
    }

    internal bool LukeMovieCardsReady(AntonRaidBattleView view)
    {
        lock (gate) return Kind == RaidKind.Luke && ReferenceEquals(current, view) &&
            view.State == AntonRaidBattleState.Reward && !view.ClockFaulted && !view.PhaseExpired &&
            (view.Phase != 0 || LukeFirstMovieReadyLocked());
    }

    internal bool CanClaimRaidReward(RaidActor actor, int phase, bool automaticLuke = false)
    {
        lock (gate)
        {
            if (!HasLukeFirstMovieLocked() || phase != 0) return true;
            if (!actor.IsCurrent || !members.ContainsKey(actor) || current.Phase != phase ||
                current.State != AntonRaidBattleState.Reward || current.ClockFaulted || current.PhaseExpired) return false;
            // 自动资产收尾仅由既有185秒权威到期放行；不伪造动画完成或牌面就绪。
            return LukeFirstMovieReadyLocked() || automaticLuke && LukeRewardRemainingLocked() == TimeSpan.Zero;
        }
    }

    void LeaveLukeFirstMovieLocked(RaidActor actor, bool disconnected)
    {
        if (disconnected || Kind != RaidKind.Luke || current.Phase != 0 ||
            !lukeSettlements.TryGetValue(0, out var settlement) || !settlement.Members.Any(member =>
                member.Character == actor.Character && member.Present)) return;
        bool before = LukeFirstMovieReadyLocked();
        lukeFirstMovieDeparted.Add(actor.Character);
        // 原主动离团发布链按view变更广播；断线不会走此分支，也不会删除已有确认。
        if (!before && LukeFirstMovieReadyLocked())
        {
            current = current with { Revision = checked(current.Revision + 1) };
            _ = AdvanceAfterRewards(Instance, current.Revision);
        }
    }
}
