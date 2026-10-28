using System.Collections.Immutable;
using DfoServer.Game.Accounts;

namespace DfoServer.Game.Pvp;

internal sealed record PvpTotalRatingReceipt(Guid MatchId, Guid TeamId, string Account, PvpArenaOutcome Outcome,
    long BeforeRevision, long Revision, PvpRatingChange Change, ImmutableArray<PvpRatingReceipt> Characters, string RuleVersion);
internal sealed record PvpTotalRatingClaim(Guid MatchId, Guid TeamId, PvpArenaOutcome Outcome,
    PvpRatingSnapshot Before, PvpRatingState Opponent, ImmutableArray<ushort> Characters, PvpTotalRatingReceipt? Receipt);
internal sealed record PvpTotalRatingPlan(PvpTotalRatingClaim Claim, PvpRatingChange Change,
    ImmutableArray<PvpRatingPlan> Characters);

internal interface IPvpTotalRatingStore
{
    PvpRatingSnapshot? ReadPvpTotalRating(AccountOwners.Lease owner, Guid teamId);
    PvpTotalRatingClaim? ReadPvpTotalRatingClaim(AccountOwners.Lease owner, Guid matchId);
    Guid[] ReadPendingPvpTotalRatings(AccountOwners.Lease owner, int limit);
    PvpTotalRatingReceipt CommitPvpTotalRating(AccountOwners.Lease owner, PvpTotalRatingPlan plan);
}

// 队伍分与尚未消费的三个角色经验在同一事务提交；先前已恢复的角色回执只读复用。
internal sealed class PvpTotalRatingSettlement(IPvpTotalRatingStore store, PvpRatingSettlement characters,
    PvpRatingCalculator calculator)
{
    public PvpRatingSnapshot Capture(AccountOwners.Lease owner, Guid teamId) => owner.Execute(() =>
    {
        var state = store.ReadPvpTotalRating(owner, teamId) ?? new(0, calculator.Initial());
        calculator.Validate(state.State);
        return state;
    });

    public PvpTotalRatingReceipt Settle(AccountOwners.Lease owner, Guid matchId) => owner.Execute(() =>
    {
        var claim = store.ReadPvpTotalRatingClaim(owner, matchId) ?? throw new InvalidOperationException("总力队伍缺少持久结算来源");
        if (claim.Receipt is { } receipt) return receipt;
        if (Capture(owner, claim.TeamId) != claim.Before) throw new InvalidOperationException("总力队伍赛前版本已失效");
        var plans = claim.Characters.Select(id => characters.Prepare(owner, id, matchId, PvpRatingMode.Total))
            .OfType<PvpRatingPlan>().ToImmutableArray();
        var change = calculator.Calculate(claim.Before.State, PvpRatingMode.Total, claim.Outcome, claim.Opponent);
        return store.CommitPvpTotalRating(owner, new(claim, change, plans));
    });

    public bool Recover(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        foreach (var match in store.ReadPendingPvpTotalRatings(owner, 32))
        {
            var claim = store.ReadPvpTotalRatingClaim(owner, match) ?? throw new InvalidOperationException("总力恢复来源丢失");
            // 先恢复本局之前的个人来源；本局三角色仍由下方队伍事务共同消费。
            foreach (ushort id in claim.Characters)
                if (!characters.RecoverBefore(owner, id, match)) return false;
            Settle(owner, match);
        }
        return store.ReadPendingPvpTotalRatings(owner, 1).Length == 0;
    });
}
