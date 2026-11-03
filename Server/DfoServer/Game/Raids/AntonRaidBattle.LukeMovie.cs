namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 各阶段分别记录真实演出确认，P1确认不能代替P2；最多两阶段八人。
    readonly HashSet<(int Phase, ushort Character)> lukeMovieCompleted = [];
    readonly HashSet<(int Phase, ushort Character)> lukeMovieDeparted = [];

    // 完整卢克流程且已有密封Present集合才具有演出屏障；领域诊断不创建不可能完成的UI义务。
    bool HasLukeFirstMovieLocked() => Kind == RaidKind.Luke && lukeFlow is not null && lukeSettlements.ContainsKey(0);

    // P2全员无资格时也须先播放成功演出，不能直接把业务Completed当成原生翻牌state4。
    // 有资格的P2仍沿原个人电影/选牌合同，不增加全团选牌前置条件。
    bool HasLukeMovieBarrierLocked() => HasFiendwarMovieBarrierLocked() || HasLukeFirstMovieLocked() && current.Phase == 0 ||
        Kind == RaidKind.Luke && lukeFlow is not null && current.Phase == 1 &&
        lukePhaseCommits.TryGetValue(1, out var committed) && committed.Members.All(member => !member.Eligible);

    bool LukeMovieBarrierReadyLocked() => lukeSettlements.TryGetValue(current.Phase, out var settlement) &&
        settlement.Members.Where(member => member.Present).All(member =>
            lukeMovieCompleted.Contains((current.Phase, member.Character)) || lukeMovieDeparted.Contains((current.Phase, member.Character)));

    internal bool CanConfirmLukeMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return Kind == RaidKind.Luke && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired &&
            lukeSettlements.TryGetValue(view.Phase, out var settlement) && settlement.Members.Any(member =>
                member.Character == actor.Character && member.Present) && !lukeMovieDeparted.Contains((view.Phase, actor.Character));
    }

    // 只有真实659=1调用；0仅请求跳过，不在此记账。发布由调用方在所有owner/Battle锁外完成。
    internal bool CompleteLukeFirstMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!CanConfirmLukeMovie(actor, view)) return false;
            if (!lukeMovieCompleted.Add((view.Phase, actor.Character))) return false;
            // 无领奖资格也须完成成功演出；全员确认后沿原奖励屏障推进，不重造资格或发空奖。
            if (LukeMovieBarrierReadyLocked()) _ = AdvanceAfterRewards(Instance, current.Revision);
            return true;
        }
    }

    internal bool LukeMovieCardsReady(AntonRaidBattleView view)
    {
        lock (gate) return Kind == RaidKind.Luke && ReferenceEquals(current, view) &&
            view.State == AntonRaidBattleState.Reward && !view.ClockFaulted && !view.PhaseExpired &&
            (view.Phase != 0 || LukeMovieBarrierReadyLocked());
    }

    internal bool CanClaimRaidReward(RaidActor actor, int phase, bool automaticLuke = false)
    {
        lock (gate)
        {
            if (HasAntonRewards) return actor.IsCurrent && members.ContainsKey(actor) && current.Phase == phase &&
                current.State == AntonRaidBattleState.Reward && !current.ClockFaulted && !current.PhaseExpired && current.AntonCardsReady;
            if (Kind == RaidKind.Fiendwar)
                return actor.IsCurrent && members.ContainsKey(actor) && current.Phase == phase &&
                    current.State == AntonRaidBattleState.Reward && !current.ClockFaulted && !current.PhaseExpired &&
                    HasFiendwarMovieBarrierLocked() && (LukeMovieBarrierReadyLocked() ||
                        automaticLuke && LukeRewardRemainingLocked() == TimeSpan.Zero);
            if (!HasLukeFirstMovieLocked() || phase != 0) return true;
            if (!actor.IsCurrent || !members.ContainsKey(actor) || current.Phase != phase ||
                current.State != AntonRaidBattleState.Reward || current.ClockFaulted || current.PhaseExpired) return false;
            // 自动资产收尾仅由既有185秒权威到期放行；不伪造动画完成或牌面就绪。
            return LukeMovieBarrierReadyLocked() || automaticLuke && LukeRewardRemainingLocked() == TimeSpan.Zero;
        }
    }

    void LeaveLukeFirstMovieLocked(RaidActor actor, bool disconnected)
    {
        if (disconnected || Kind is not (RaidKind.Luke or RaidKind.Fiendwar) || !HasLukeMovieBarrierLocked() ||
            !lukeSettlements.TryGetValue(current.Phase, out var settlement) || !settlement.Members.Any(member =>
                member.Character == actor.Character && member.Present)) return;
        bool before = LukeMovieBarrierReadyLocked();
        lukeMovieDeparted.Add((current.Phase, actor.Character));
        // 原主动离团发布链按view变更广播；断线不会走此分支，也不会删除已有确认。
        if (!before && LukeMovieBarrierReadyLocked())
        {
            current = current with { Revision = checked(current.Revision + 1) };
            _ = AdvanceAfterRewards(Instance, current.Revision);
        }
    }
}
