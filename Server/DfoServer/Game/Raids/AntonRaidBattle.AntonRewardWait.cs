using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly AntonRewardCards antonCards = new();
    internal bool SelectableRewardCards => HasAntonRewards || Kind == RaidKind.Luke && lukeResourceRewards is not null ||
        Kind == RaidKind.Fiendwar && lukeCompletion is not null;

    internal bool MatchesAntonCardReplay(PreparedRaidReward reward, byte requested)
    {
        lock (gate) return SelectableRewardCards && reward.Instance == Instance &&
            antonCards.MatchesReplay(reward.Phase, reward.Character, requested, reward.Card);
    }

    internal RaidRewardOffer? SelectAntonRewardCard(RaidActor actor, int phase, byte kind, byte requested, byte squad, bool automaticLuke = false)
    {
        lock (gate)
        {
            if (!SelectableRewardCards || !actor.IsCurrent || current.State != AntonRaidBattleState.Reward ||
                current.Phase != phase || current.ClockFaulted || pendingTransition is not null ||
                !members.TryGetValue(actor, out var policy) || !CanClaimRaidReward(actor, phase, automaticLuke) ||
                (HasAntonRewards ? AntonEligibility(actor.Character, phase) : LukeEligibility(actor.Character, phase)) is not { Eligible: true } ||
                policy[phase].Offers.SingleOrDefault(offer => offer.Kind == kind && offer.Card == 0) is not { } frozen)
                return null;
            if (Kind == RaidKind.Fiendwar)
            {
                if (requested > 3) return null;
                // 漩涡为每角色冻结的原牌面；只改变客户端展示位置，不改变物品或资格。
                if (!fiendwarRewardCards.TryGetValue(actor.Character, out byte selected))
                    fiendwarRewardCards.Add(actor.Character, selected = requested);
                return frozen with { Card = selected, Display = frozen.Display.Select(row => row with { Card = selected }).ToImmutableArray() };
            }
            if (antonCards.Select(phase, actor.Character, squad, requested) is not { } card) return null;
            // 只移动已冻结Card0产物的展示位置，不重抽、不改未知code/flag。
            return frozen with { Card = card, Display = frozen.Display.Select(row => row with { Card = card }).ToImmutableArray() };
        }
    }

    readonly HashSet<ushort> antonMovieCompleted = [];
    long? antonCardAnchor;

    internal bool CanConfirmAntonMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasAntonRewards && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired && antonPhaseCommits.ContainsKey(view.Phase);
    }

    internal bool CompleteAntonMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!CanConfirmAntonMovie(actor, view) || current.AntonCardsReady || !antonMovieCompleted.Add(actor.Character)) return false;
            // 资格提交时的成员集合是屏障；断线不冒充完成，重连沿用原期限。
            if (antonSettlements[current.Phase].Members.All(row => antonMovieCompleted.Contains(row.Character)))
                OpenAntonCardsLocked();
            return true;
        }
    }

    void OpenAntonCardsLocked()
    {
        antonCardAnchor = lukeClock.GetTimestamp();
        current = current with { AntonCardsReady = true, Revision = current.Revision + 1 };
        ScheduleLukeClockLocked();
    }

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
        ? TimeSpan.FromTicks(Math.Max(0, (TimeSpan.FromSeconds(120) - lukeClock.GetElapsedTime(anchor)).Ticks)) : null;

    long? antonRewardAnchor;
    int antonRewardPhase = -1;
    bool antonRewardsAbandoned;

    // 本服政策：影片最多等待120秒（客户端90秒保护加标题/传输余量），不是原厂时长。
    // 选牌期限另从屏障结束起算，沿PVF MOVIE END TIME，到期在线成员走原领取链。
    // 只在阶段资格事务提交后锚定，重放、回城和重连均不能续期。
    void AnchorAntonRewardWaitLocked()
    {
        if (!HasAntonRewards || current.State != AntonRaidBattleState.Reward || current.PhaseExpired) return;
        antonRewardPhase = current.Phase;
        antonRewardAnchor = lukeClock.GetTimestamp();
        antonCardAnchor = null;
        antonMovieCompleted.Clear();
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
            antonRewardPhase != current.Phase || antonRewardAnchor is null) return null;
        if (!current.AntonCardsReady) return AntonMovieRemainingLocked();
        if (antonCardAnchor is not { } anchor) return null;
        int seconds = script.Headers.Single(row => row.Tag == "[movie end time]").Values[current.Phase].Number;
        var remaining = TimeSpan.FromSeconds(seconds) - lukeClock.GetElapsedTime(anchor);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    internal bool AntonAutoRewardDue
    { get { lock (gate) return current.AntonCardsReady && AntonRewardWaitRemainingLocked() == TimeSpan.Zero; } }

    internal RaidActor[] AntonAutoRewardActors()
    { lock (gate) return members.Keys.ToArray(); }

    bool ExpireAntonRewardWaitLocked()
    {
        if (!current.AntonCardsReady || AntonRewardWaitRemainingLocked() is not { } remaining || remaining > TimeSpan.Zero) return false;
        // 放弃集合与真实领取确认分离：不写奖励回执，不标记资产已发放。
        antonRewardsAbandoned = true;
        return AdvanceAfterRewards(Instance, current.Revision) is not null;
    }
}
