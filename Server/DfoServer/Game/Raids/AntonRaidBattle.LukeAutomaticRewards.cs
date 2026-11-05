namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    long? lukeRewardAnchor;
    int lukeRewardPhase = -1;

    internal bool HasPreyTeamAutomaticRewards => Kind == RaidKind.Prey && !PreyGuide &&
        lukeCompletion?.Source == PreyGuideRewards.TeamSource;

    internal bool PreyTeamRewardDue
    { get { lock (gate) return HasPreyTeamAutomaticRewards && LukeRewardRemainingLocked() == TimeSpan.Zero; } }

    // 普雷结果窗会锁存完整团队表；首个真实演出完成后交付全团权益，各人演出和终局另行确认。
    TimeSpan? LukeRewardRemainingLocked()
    {
        var grace = LukeRewardGraceRemainingLocked();
        if (grace is null) return null;
        // 普通普雷一阶段没有原生翻牌请求；全部正式Run退场后直接履行原资格，再进入下一阶段。
        if (HasPreyTeamAutomaticRewards && current.Phase == 0)
            return dungeonRuns.Count == 0 && lukePhaseCommits.ContainsKey(0) ? TimeSpan.Zero : null;
        if (HasPreyTeamAutomaticRewards && current.Phase == 1 && PreyMovieRewardsRequestedLocked() &&
            (!AllRewardsConfirmed(1) || LukeMovieBarrierReadyLocked()))
            return TimeSpan.Zero;
        return grace;
    }

    // 独立保留原资源演出最大值加120秒的期限；不能把某人的立即调度当成全团超时。
    TimeSpan? LukeRewardGraceRemainingLocked()
    {
        if (lukeFlow is null && !(Kind == RaidKind.Fiendwar && HasFiendwarMovieBarrierLocked()) && !HasPreyTeamAutomaticRewards ||
            current.ClockFaulted || current.State != AntonRaidBattleState.Reward) return null;
        if (lukeRewardPhase != current.Phase)
        { lukeRewardPhase = current.Phase; lukeRewardAnchor = lukeClock.GetTimestamp(); }
        int movie = script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(row => row.Number);
        return TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(checked(movie + 120)) -
            lukeClock.GetElapsedTime(lukeRewardAnchor!.Value)).Ticks));
    }

    internal (string Account, RaidPhaseEligibility Eligibility)[] DueLukeRewards()
    {
        lock (gate)
        {
            if (LukeRewardRemainingLocked() != TimeSpan.Zero || !lukePhaseCommits.TryGetValue(current.Phase, out var committed)) return [];
            if (HasPreyTeamAutomaticRewards && current.Phase == 1 && !PreyMovieRewardsRequestedLocked() &&
                LukeRewardGraceRemainingLocked() != TimeSpan.Zero) return [];
            return committed.Members.Where(row => row.Eligible)
                .Select(row => (originalLukeMembers[row.Character].Owner.Account, row)).ToArray();
        }
    }

    internal bool HasConfirmedLukeReward(ushort character, int phase, byte kind)
    { lock (gate) return rewards.Contains((character, phase, kind)); }

    internal void ConfirmAutomaticLukeReward(RaidPhaseEligibility eligibility, PreparedRaidReward prepared)
    {
        lock (gate)
        {
            byte kind = prepared.Kind;
            if (rewards.Contains((eligibility.Character, eligibility.Phase, kind))) return;
            if (Kind is not (RaidKind.Luke or RaidKind.Fiendwar) && !HasPreyTeamAutomaticRewards ||
                Kind == RaidKind.Prey && eligibility.Source != PreyGuideRewards.TeamSource ||
                current.State != AntonRaidBattleState.Reward || eligibility.Instance != Instance ||
                eligibility.Phase != current.Phase || LukeEligibility(eligibility.Character, eligibility.Phase) is not { } actual ||
                !SameLukeEligibility(actual, eligibility) || !actual.Eligible ||
                prepared.Instance != Instance || prepared.RaidType != Kind || prepared.Phase != eligibility.Phase ||
                prepared.Character != eligibility.Character || prepared.Source != eligibility.Source || prepared.Week != eligibility.Week ||
                actual.Offers.SingleOrDefault(row => row.Kind == kind &&
                    row.Card == (SelectableRewardCards ? 0 : prepared.Card)) is not { } offer ||
                !offer.Grants.SequenceEqual(prepared.Grants))
                throw new InvalidOperationException("自动领奖回执不属于当前阶段");
            lukeSubmittedRewards.TryAdd((prepared.Phase, prepared.Character, kind), prepared);
            rewards.Add((eligibility.Character, eligibility.Phase, kind));
        }
    }
}
