namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    long? lukeRewardAnchor;
    int lukeRewardPhase = -1;

    // 原资源四段演出最长65秒；统一再留120秒选牌，不猜四列与阶段的映射。
    TimeSpan? LukeRewardRemainingLocked()
    {
        if (lukeFlow is null || current.ClockFaulted || current.State != AntonRaidBattleState.Reward) return null;
        if (lukeRewardPhase != current.Phase)
        { lukeRewardPhase = current.Phase; lukeRewardAnchor = lukeClock.GetTimestamp(); }
        int movie = script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(row => row.Number);
        return TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(checked(movie + 120)) -
            lukeClock.GetElapsedTime(lukeRewardAnchor!.Value)).Ticks));
    }

    internal (string Account, RaidPhaseEligibility Eligibility)[] DueLukeRewards()
    {
        lock (gate) return LukeRewardRemainingLocked() == TimeSpan.Zero && lukePhaseCommits.TryGetValue(current.Phase, out var committed)
            ? committed.Members.Where(row => row.Eligible).Select(row => (originalLukeMembers[row.Character].Owner.Account, row)).ToArray() : [];
    }

    internal bool HasConfirmedLukeReward(ushort character, int phase, byte kind)
    { lock (gate) return rewards.Contains((character, phase, kind)); }

    internal void ConfirmAutomaticLukeReward(RaidPhaseEligibility eligibility, byte kind)
    {
        lock (gate)
        {
            if (rewards.Contains((eligibility.Character, eligibility.Phase, kind))) return;
            if (Kind != RaidKind.Luke || current.State != AntonRaidBattleState.Reward || eligibility.Instance != Instance ||
                eligibility.Phase != current.Phase || LukeEligibility(eligibility.Character, eligibility.Phase) is not { } actual ||
                !SameLukeEligibility(actual, eligibility) || !actual.Eligible || !actual.Offers.Any(row => row.Kind == kind))
                throw new InvalidOperationException("自动领奖回执不属于当前阶段");
            rewards.Add((eligibility.Character, eligibility.Phase, kind));
        }
    }
}
