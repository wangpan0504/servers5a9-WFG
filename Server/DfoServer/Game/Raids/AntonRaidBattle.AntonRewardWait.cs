namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<int, ushort> antonDeaths = [];
    readonly Dictionary<int, RaidResultDisplay> antonResults = [];

    // Session只在接受一次真实死亡后调用；重复死亡请求在Session内已拒绝。
    internal void RecordAntonDeath(RaidActor actor)
    {
        lock (gate)
            if (HasAntonRewards && actor.IsCurrent && members.ContainsKey(actor) &&
                current.State == AntonRaidBattleState.Fighting && pendingTransition is null)
                antonDeaths[current.Phase] = (ushort)Math.Min(ushort.MaxValue, antonDeaths.GetValueOrDefault(current.Phase) + 1);
    }

    internal RaidResultDisplay? AntonResult(int phase)
    { lock (gate) return antonResults.GetValueOrDefault(phase); }

    TimeSpan? AntonMovieRemainingLocked() => !current.AntonCardsReady && antonRewardAnchor is { } anchor
        ? TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(2) - lukeClock.GetElapsedTime(anchor)).Ticks)) : null;

    long? antonRewardAnchor;
    int antonRewardPhase = -1;
    bool antonRewardsAbandoned;

    // 本服兜底政策：沿PVF当前阶段MOVIE END TIME秒数限制领奖等待；不冒充原服自动发奖。
    // 只在阶段资格事务提交后锚定，重放、回城和重连均不能续期。
    void AnchorAntonRewardWaitLocked()
    {
        if (!HasAntonRewards || current.State != AntonRaidBattleState.Reward || current.PhaseExpired) return;
        antonRewardPhase = current.Phase;
        antonRewardAnchor = lukeClock.GetTimestamp();
        antonRewardsAbandoned = false;
    }

    void FreezeAntonResultLocked()
    {
        ushort deaths = antonDeaths.GetValueOrDefault(current.Phase);
        // DEAD COUNT四档与95 rank 2..5动画一致；>14保守使用最低档，待实机确认。
        byte rank = deaths <= 1 ? (byte)2 : deaths <= 5 ? (byte)3 : deaths <= 9 ? (byte)4 : (byte)5;
        uint seconds = lukePhaseAnchor is { } start ? checked((uint)Math.Max(0, Math.Floor(lukeClock.GetElapsedTime(start).TotalSeconds))) : 0;
        antonResults[current.Phase] = new(0, checked((byte)current.Phase), seconds, deaths, rank, 0);
    }

    TimeSpan? AntonRewardWaitRemainingLocked()
    {
        if (!HasAntonRewards || current.ClockFaulted || current.State != AntonRaidBattleState.Reward ||
            antonRewardPhase != current.Phase || antonRewardAnchor is not { } anchor) return null;
        int seconds = script.Headers.Single(row => row.Tag == "[movie end time]").Values[current.Phase].Number;
        var remaining = TimeSpan.FromSeconds(seconds) - lukeClock.GetElapsedTime(anchor);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    bool ExpireAntonRewardWaitLocked()
    {
        if (AntonRewardWaitRemainingLocked() is not { } remaining || remaining > TimeSpan.Zero) return false;
        // 放弃集合与真实领取确认分离：不写奖励回执，不标记资产已发放。
        antonRewardsAbandoned = true;
        return AdvanceAfterRewards(Instance, current.Revision) is not null;
    }
}
