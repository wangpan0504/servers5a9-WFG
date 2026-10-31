using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal interface IAntonRaidLifecycleStore
{
    ImmutableArray<RaidPhaseEligibility> CommitAntonPhase(AntonRaidBattle.AntonPhaseSettlement candidate);
    bool IsRetryable(Exception error) => false;
}

internal sealed partial class AntonRaidBattle
{
    readonly AntonResourceRewards? antonRewards;
    readonly Dictionary<ushort, RaidActor> originalAntonMembers = [];
    readonly Dictionary<int, AntonPhaseSettlement> antonSettlements = [];
    readonly Dictionary<int, ImmutableArray<RaidPhaseEligibility>> antonPhaseCommits = [];
    internal bool HasAntonRewards => antonRewards is not null;

    internal void RetryAntonSettlement()
    {
        lock (gate)
        {
            if (lukeClockLease is not { IsStopped: false } lease || PendingAntonSettlement() is null) return;
            lease.SetRetry(TimeSpan.FromSeconds(1));
            ScheduleLukeClockLocked();
        }
    }

    // 只有已验证的CLEAR PHASE候选可签发，不允许网络自报成员、事件或奖励计划。
    internal sealed class AntonPhaseSettlement
    {
        public Guid Instance { get; }
        public Guid Event { get; } = Guid.NewGuid();
        public int Phase { get; }
        public bool TimedOut { get; }
        public DateTimeOffset Anchor { get; }
        public int Week { get; }
        public ImmutableArray<(ushort Character, string Account, RaidPhaseRewardPolicy Policy)> Members { get; }
        private AntonPhaseSettlement(AntonRaidBattle battle, int phase)
        {
            battle.FreezeAntonResultLocked();
            Instance = battle.Instance; Phase = phase; Anchor = battle.lukeClock.GetUtcNow();
            TimedOut = battle.pendingTransition!.PhaseExpired;
            Week = battle.rewardWeek ?? Game.Dungeons.DungeonIncountPolicy.Period(Anchor).Week;
            Members = battle.originalAntonMembers.Values.OrderBy(actor => actor.Character)
                .Where(actor => actor.IsCurrent && battle.members.ContainsKey(actor))
                .Select(actor => (actor.Character, actor.Owner.Account, battle.members[actor][phase])).ToImmutableArray();
        }
        internal static AntonPhaseSettlement Capture(AntonRaidBattle battle, int phase)
        {
            if (battle.antonRewards is null || battle.Kind != RaidKind.Anton || phase is not (0 or 1) ||
                battle.pendingTransition is not { } terminal || terminal.Phase != phase ||
                terminal.State != AntonRaidBattleState.Reward && !terminal.PhaseExpired)
                throw new InvalidOperationException("安徒恩缺少已封存阶段终结事实");
            if (battle.antonSettlements.TryGetValue(phase, out var old)) return old;
            var result = new AntonPhaseSettlement(battle, phase);
            battle.antonSettlements.Add(phase, result); return result;
        }
    }

    internal AntonPhaseSettlement? PendingAntonSettlement()
    {
        lock (gate) return !current.ClockFaulted && antonSettlements.TryGetValue(current.Phase, out var candidate) &&
            !antonPhaseCommits.ContainsKey(current.Phase) ? candidate : null;
    }

    internal void AcceptAntonSettlement(AntonPhaseSettlement candidate, ImmutableArray<RaidPhaseEligibility> receipt)
    {
        lock (gate)
        {
            if (!antonSettlements.TryGetValue(candidate.Phase, out var expected) || !ReferenceEquals(candidate, expected) ||
                receipt.Length != candidate.Members.Length || receipt.Select(row => row.Character).Distinct().Count() != receipt.Length ||
                receipt.Any(row => row.Instance != Instance || row.RaidType != RaidKind.Anton || row.Phase != candidate.Phase ||
                    row.Event != candidate.Event || row.Week != candidate.Week || row.Anchor != candidate.Anchor.ToUnixTimeSeconds() ||
                    candidate.TimedOut && (row.Eligible || row.CountsAsClear || row.Reason != "phase-timeout") ||
                    !candidate.Members.Any(member => member.Character == row.Character && member.Policy.Source == row.Source &&
                        SameLukeOffers(member.Policy.Offers, row.Offers))))
                throw new InvalidDataException("安徒恩阶段回执不匹配原封存候选");
            if (antonPhaseCommits.ContainsKey(candidate.Phase)) return;
            antonPhaseCommits.Add(candidate.Phase, receipt);
            if (pendingTransition is not { } pending) throw new InvalidOperationException("安徒恩阶段候选已丢失");
            current = pending with { Revision = current.Revision + 1 };
            pendingTransition = null;
            AnchorAntonRewardWaitLocked();
            ScheduleLukeClockLocked();
        }
    }

    internal RaidPhaseEligibility? AntonEligibility(ushort character, int phase)
    {
        lock (gate) return antonPhaseCommits.TryGetValue(phase, out var receipt) ? receipt.FirstOrDefault(row => row.Character == character) : null;
    }
}
