using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

internal sealed class RaidLifecycleRejectedException(string reason) : InvalidOperationException(reason)
{
    public string Reason { get; } = reason;
}
internal enum LukeParticipationCutoff { TerminalCommittedFacts }
// 所有额度和周期均为显式本服策略；门票周日删除不能替代通关周期证据。
internal sealed record LukePhaseCompletionPolicy(int MinimumCommittedRuns, int WeeklyEligibilityLimit, RaidPhaseRewardPolicy Rewards);
internal sealed record RaidWeeklyCycle(DayOfWeek Day, TimeOnly Time, TimeSpan Offset)
{
    internal bool Valid => Enum.IsDefined(Day) && Offset.Ticks % TimeSpan.TicksPerMinute == 0 &&
        Offset >= TimeSpan.FromHours(-14) && Offset <= TimeSpan.FromHours(14);
    internal int Key(DateTimeOffset anchor)
    {
        if (!Valid || anchor.Year < 1970) throw new InvalidDataException("显式攻坚周期无效");
        var local = anchor.ToOffset(Offset);
        var beginning = local.Date.AddDays(-((7 + (int)local.DayOfWeek - (int)Day) % 7)).Add(Time.ToTimeSpan());
        if (local.DateTime < beginning) beginning = beginning.AddDays(-7);
        return beginning.Year * 10000 + beginning.Month * 100 + beginning.Day;
    }
}
internal sealed record LukeRaidCompletionPolicy(string Source, int WeeklyClearLimit, RaidWeeklyCycle Cycle,
    ImmutableArray<LukePhaseCompletionPolicy> Phases, LukeParticipationCutoff Cutoff);
internal sealed record LukePhaseMember(ushort Character, string Account, int CommittedRuns, bool Present)
{
    public RaidPhaseRewardPolicy? FrozenRewards { get; init; }
    public ImmutableArray<RaidRewardOffer> Offers { get; init; }
}
internal sealed record LukePhaseCommit(ImmutableArray<RaidPhaseEligibility> Members);
internal interface ILukeRaidLifecycleStore
{
    LukePhaseCommit CommitLukePhase(AntonRaidBattle.LukePhaseSettlement candidate);
    // 存储实现判定可恢复错误；默认不把未知异常当作忙锁。
    bool IsRetryable(Exception error) => false;
}

internal sealed partial class AntonRaidBattle
{
    LukeRaidCompletionPolicy? lukeCompletion;
    TimeProvider lukeClock = TimeProvider.System;
    readonly Dictionary<ushort, RaidActor> originalLukeMembers = [];
    readonly Dictionary<(int Phase, ushort Character), int> lukeParticipation = [];
    readonly Dictionary<int, LukePhaseSettlement> lukeSettlements = [];
    readonly Dictionary<int, LukePhaseCommit> lukePhaseCommits = [];
    readonly ImmutableDictionary<ushort, ImmutableArray<RaidPhaseRewardPolicy>> memberRewardPolicies;
    AccountMaterialDefinition? undefeatedSongLimit;

    internal static bool ValidLukeCompletion(LukeRaidCompletionPolicy policy) => !string.IsNullOrWhiteSpace(policy.Source) &&
        policy.WeeklyClearLimit > 0 && policy.Cycle is { Valid: true } && policy.Cutoff == LukeParticipationCutoff.TerminalCommittedFacts &&
        !policy.Phases.IsDefault && policy.Phases.Length is 1 or 2 && policy.Phases.All(phase => phase is not null && phase.Rewards is not null && phase.MinimumCommittedRuns >= 0 && phase.WeeklyEligibilityLimit > 0 &&
            !string.IsNullOrWhiteSpace(phase.Rewards.Source) && !phase.Rewards.Offers.IsDefaultOrEmpty && phase.Rewards.Offers.Length <= 32 &&
            phase.Rewards.Offers.All(offer => offer is not null && offer.Kind is 0 or 1 && offer.Display.IsEmpty && !offer.Grants.IsDefaultOrEmpty && offer.Grants.Length <= 32 && (offer.LukeResult is null || offer.LukeResult.Matches(offer)))) &&
        policy.Phases.Select((phase, index) => phase.Rewards.Result.Phase == index && phase.Rewards.Result.Result is 0 or 2 &&
            phase.Rewards.Offers.Select(offer => (offer.Kind, offer.Card)).Distinct().Count() == phase.Rewards.Offers.Length).All(value => value);

    // 只能由本Battle在权威终结点密封；外部不能指定成员、事件、anchor或参与数量。
    internal sealed class LukePhaseSettlement
    {
        public Guid Instance { get; }
        public RaidKind RaidType { get; }
        public Guid Event { get; } = Guid.NewGuid();
        public int Phase { get; }
        public DateTimeOffset Anchor { get; }
        public int Week { get; }
        public uint Seconds { get; }
        public string Source { get; }
        public int WeeklyClearLimit { get; }
        public bool FinalPhase { get; }
        public LukePhaseCompletionPolicy Policy { get; }
        public ImmutableArray<LukePhaseMember> Members { get; }
        public ImmutableDictionary<ushort, ImmutableArray<ushort>> GuildParties { get; }
        public bool FiendwarNoFacilities { get; }
        public bool FiendwarNoBaseThreat { get; }
        public AccountMaterialDefinition? SongLimit { get; }
        private LukePhaseSettlement(AntonRaidBattle battle, int phase)
        {
            var policy = battle.lukeCompletion!;
            Instance = battle.Instance; RaidType = battle.Kind; Phase = phase; FinalPhase = phase + 1 == battle.PhaseCount; Anchor = battle.lukeClock.GetUtcNow();
            SongLimit = battle.undefeatedSongLimit;
            Week = policy.Cycle.Key(Anchor); Source = policy.Source; WeeklyClearLimit = policy.WeeklyClearLimit;
            Seconds = battle.lukePhaseAnchor is { } start
                ? checked((uint)Math.Max(0, Math.Floor(battle.lukeClock.GetElapsedTime(start).TotalSeconds))) : 0;
            Policy = policy.Phases[phase];
            GuildParties = battle.guildClearParties.ToImmutableDictionary();
            FiendwarNoFacilities = battle.Kind == RaidKind.Fiendwar && battle.fiendwarDefinition is { Guide: false } &&
                battle.current.Fiendwar is { FacilitiesDestroyed: false };
            FiendwarNoBaseThreat = battle.Kind == RaidKind.Fiendwar && battle.fiendwarDefinition is { Guide: false } &&
                battle.current.Fiendwar is { BaseThreatened: false };
            if (Week <= 0 || Anchor.ToUnixTimeSeconds() < 0) throw new InvalidDataException("卢克终结周期或时间无效");
            Members = battle.originalLukeMembers.Values.OrderBy(actor => actor.Character).Select(actor =>
                new LukePhaseMember(actor.Character, actor.Owner.Account, battle.lukeParticipation.GetValueOrDefault((phase, actor.Character)),
                    actor.IsCurrent && battle.members.ContainsKey(actor))
                { FrozenRewards = battle.memberRewardPolicies.GetValueOrDefault(actor.Character).IsDefaultOrEmpty
                    ? null : battle.memberRewardPolicies[actor.Character][phase],
                    Offers = battle.originalLukePolicies.GetValueOrDefault(actor.Character, policy.Phases.Select(p => p.Rewards).ToImmutableArray())[phase].Offers }).ToImmutableArray();
        }
        internal RaidPhaseRewardPolicy RewardsFor(ushort character)
        {
            var member = Members.Single(member => member.Character == character);
            return member.FrozenRewards ?? (member.Offers.IsDefault ? Policy.Rewards : Policy.Rewards with { Offers = member.Offers });
        }
        internal static LukePhaseSettlement Capture(AntonRaidBattle battle, int phase)
        {
            lock (battle.gate)
            {
                if (battle.Kind is not (RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar) || battle.lukeCompletion is null || phase is not (0 or 1) || phase != battle.current.Phase ||
                    battle.pendingTransition is not { State: AntonRaidBattleState.Reward } terminal || terminal.Phase != phase ||
                    battle.originalLukeMembers.Count < 1 ||
                    battle.originalLukeMembers.Count > (battle.Kind == RaidKind.Prey && !battle.PreyGuide ? 12 : 8))
                    throw new InvalidOperationException("不存在可密封攻坚终结事实");
                if (battle.lukeSettlements.TryGetValue(phase, out var existing)) return existing;
                var result = new LukePhaseSettlement(battle, phase);
                battle.lukeSettlements.Add(phase, result);
                return result;
            }
        }
    }

    internal static bool SameLukeOffers(ImmutableArray<RaidRewardOffer> left, ImmutableArray<RaidRewardOffer> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind && pair.First.Card == pair.Second.Card && SameRewardDisplay(pair.First.Display, pair.Second.Display) &&
            pair.First.Grants.SequenceEqual(pair.Second.Grants) && pair.First.LukeResult == pair.Second.LukeResult &&
            pair.First.LukeAdditionalResults.SequenceEqual(pair.Second.LukeAdditionalResults) &&
            pair.First.PreyDisplay.SequenceEqual(pair.Second.PreyDisplay));
    // Anton正式600含嵌套不可变数组；持久JSON往返后须比内容，不能要求空表或数组引用相同。
    static bool SameRewardDisplay(ImmutableArray<RaidRewardDisplayRow> left, ImmutableArray<RaidRewardDisplayRow> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.RawOwner == pair.Second.RawOwner && pair.First.Card == pair.Second.Card &&
            !pair.First.Items.IsDefault && !pair.Second.Items.IsDefault && pair.First.Items.SequenceEqual(pair.Second.Items));

    static bool SameLukeEligibility(RaidPhaseEligibility left, RaidPhaseEligibility right) =>
        left.Instance == right.Instance && left.RaidType == right.RaidType && left.Phase == right.Phase && left.Event == right.Event &&
        left.Anchor == right.Anchor && left.Character == right.Character && left.CountsAsClear == right.CountsAsClear &&
        left.Eligible == right.Eligible && left.Reason == right.Reason && left.Source == right.Source && left.Week == right.Week &&
        SameLukeOffers(left.Offers, right.Offers);

    internal RaidPhaseEligibility? LukeEligibility(ushort character, int phase)
    {
        lock (gate) return lukePhaseCommits.GetValueOrDefault(phase)?.Members.SingleOrDefault(row => row.Character == character);
    }
    internal uint? LukeResultSeconds(int phase)
    { lock (gate) return lukeSettlements.GetValueOrDefault(phase)?.Seconds; }
    bool RequiresReward(RaidActor actor, int phase) => Kind is not (RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar) || lukeCompletion is null ||
        lukePhaseCommits.GetValueOrDefault(phase)?.Members.Any(row => row.Character == actor.Character && row.Eligible) == true;

    bool AllRewardsConfirmed(int phase)
    {
        // 断线/离团不撤回已密封领奖义务；不能用当前在线members过滤后跳过未领奖者。
        if (Kind is RaidKind.Luke or RaidKind.Prey or RaidKind.Fiendwar && lukeCompletion is not null)
            return lukePhaseCommits.TryGetValue(phase, out var committed) && committed.Members.Where(row => row.Eligible)
                .All(row => row.Offers.Select(offer => offer.Kind).Distinct().All(kind => rewards.Contains((row.Character, phase, kind))));
        if (Kind == RaidKind.Anton && antonRewards is not null)
            return antonPhaseCommits.TryGetValue(phase, out var antonCommitted) &&
                (antonRewardsAbandoned && antonRewardPhase == phase || antonCommitted.Where(row => row.Eligible)
                    .All(row => row.Offers.Select(offer => offer.Kind).Distinct().All(kind => rewards.Contains((row.Character, phase, kind)))));
        return members.All(member => member.Value[phase].Offers.Select(offer => offer.Kind).Distinct()
            .All(kind => rewards.Contains((member.Key.Character, phase, kind))));
    }

    internal LukePhaseSettlement? PendingLukeSettlement()
    {
        lock (gate) return !current.ClockFaulted && lukeSettlements.GetValueOrDefault(current.Phase) is { } candidate && !lukePhaseCommits.ContainsKey(candidate.Phase)
            ? candidate : null;
    }

    internal bool RequiresLukeCompletedPhaseReturn(RaidActor actor, Guid instance, int phase)
    {
        lock (gate) return (Kind == RaidKind.Luke || Kind == RaidKind.Prey && !PreyGuide) &&
            Instance == instance && current.Phase == phase &&
            !current.ClockFaulted && actor.IsCurrent && members.ContainsKey(actor) &&
            (pendingTransition is { State: AntonRaidBattleState.Reward } terminal && terminal.Phase == phase ||
                current.State == AntonRaidBattleState.Reward) &&
            lukePhaseCommits.ContainsKey(phase) &&
            // 阶段资格时钟也会发布终结view；所有原通关事件先入全团FIFO再允许退场。
            !dungeonRuns.Values.Any(run => run.Phase == phase && run.LukeClear is { Published: false });
    }

    internal AntonRaidBattleChange? AcceptLukeSettlement(LukePhaseSettlement candidate, LukePhaseCommit receipt)
    {
        lock (gate)
        {
            if (!lukeSettlements.TryGetValue(candidate.Phase, out var expected) || !ReferenceEquals(expected, candidate) ||
                receipt.Members.Length != candidate.Members.Length || receipt.Members.Select(row => row.Character).Distinct().Count() != receipt.Members.Length ||
                receipt.Members.Any(row => row.Instance != Instance || row.RaidType != Kind || row.Phase != candidate.Phase ||
                    row.Event != candidate.Event || row.Anchor != candidate.Anchor.ToUnixTimeSeconds() || row.Week != candidate.Week ||
                    !candidate.Members.Any(member => member.Character == row.Character) ||
                    row.Source != candidate.RewardsFor(row.Character).Source || !AcceptedSongFace(row)))
                throw new InvalidDataException("卢克阶段回执不属于原密封候选");
            if (lukePhaseCommits.TryGetValue(candidate.Phase, out var previous))
            {
                if (!previous.Members.Zip(receipt.Members).All(pair => SameLukeEligibility(pair.First, pair.Second))) throw new InvalidDataException("卢克阶段回执发生变化");
                return null;
            }
            lukePhaseCommits.Add(candidate.Phase, receipt);
            if (candidate.SongLimit is not null) PublishSealedSongOffers(candidate.Phase, receipt);
            var transition = CommitPendingTransition();
            if (transition is not null || !(Kind == RaidKind.Luke || Kind == RaidKind.Prey && !PreyGuide) ||
                pendingTransition is not { State: AntonRaidBattleState.Reward }) return transition;
            // 持久资格确认后发布退场事实；正式Run仍由原退出路径释放，不能越过资产屏障直接翻牌。
            var before = current;
            current = current with { Revision = checked(current.Revision + 1) };
            return new(before, current);

            bool AcceptedSongFace(RaidPhaseEligibility row)
            {
                var expectedOffers = candidate.RewardsFor(row.Character).Offers;
                return SameLukeOffers(row.Offers, expectedOffers) ||
                    candidate.SongLimit is not null && RaidSongQuota.IsOfferReduction(row.Offers, expectedOffers);
            }
        }
    }

    // 调用方已持有 gate。只改本阶段已密封的牌面，不改开团时保存的原始 memberRewardPolicies。
    void PublishSealedSongOffers(int phase, LukePhaseCommit receipt)
    {
        foreach (var row in receipt.Members)
        {
            if (!row.Eligible) continue;
            RaidActor? actor = null;
            foreach (var key in members.Keys)
                if (key.Character == row.Character) { actor = key; break; }
            if (actor is null || !members.TryGetValue(actor, out var policies) || (uint)phase >= (uint)policies.Length) continue;
            if (SameLukeOffers(policies[phase].Offers, row.Offers)) continue;
            var next = policies.SetItem(phase, policies[phase] with { Offers = row.Offers });
            members[actor] = next;
            if (originalLukePolicies.ContainsKey(row.Character))
                originalLukePolicies[row.Character] = next;
        }
    }
}
