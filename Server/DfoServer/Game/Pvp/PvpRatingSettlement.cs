using DfoServer.Game.Accounts;

namespace DfoServer.Game.Pvp;

internal sealed record PvpRatingSnapshot(long Revision, PvpRatingState State);
internal sealed record PvpRatingReceipt(Guid MatchId, ushort CharacterId, string Account, PvpArenaOutcome Outcome,
    long BeforeRevision, long Revision, PvpRatingChange Change, string RuleVersion)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PvpQuestAdvance? QuestAdvance { get; init; }
}
internal sealed record PvpQuestAdvance(long BeforeRevision, long Revision,
    System.Collections.Immutable.ImmutableArray<DfoServer.Game.Characters.ActiveMainQuest> Before,
    System.Collections.Immutable.ImmutableArray<DfoServer.Game.Characters.ActiveMainQuest> After);
internal sealed record PvpRatingClaim(Guid MatchId, ushort CharacterId, PvpRatingMode Mode, PvpArenaOutcome Outcome,
    PvpRatingReceipt? Receipt)
{
    public PvpRatingSnapshot? FrozenBefore { get; init; }
    public PvpRatingState? Opponent { get; init; }
}
internal sealed record PvpRatingPlan(PvpRatingClaim Claim, PvpRatingSnapshot Before, PvpRatingChange Change);

internal interface IPvpRatingStore
{
    PvpRatingSnapshot? ReadPvpRating(AccountOwners.Lease owner, ushort characterId);
    PvpRatingClaim? ReadPvpRatingClaim(AccountOwners.Lease owner, ushort characterId, Guid matchId);
    Guid[] ReadPendingPvpRatings(AccountOwners.Lease owner, ushort characterId, int limit);
    PvpRatingReceipt CommitPvpRating(AccountOwners.Lease owner, PvpRatingPlan plan);
}

// 每名角色仅在自己的有效owner下消费结算来源；整局事实与来源先原子保存，离线者重连后恢复。
internal sealed class PvpRatingSettlement(IPvpRatingStore store, PvpRatingCalculator calculator)
{
    internal int Grade(PvpRatingSnapshot snapshot) => calculator.Grade(snapshot.State);
    public PvpRatingSnapshot Capture(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        var snapshot = store.ReadPvpRating(owner, characterId) ?? new(0, calculator.Initial());
        calculator.Validate(snapshot.State);
        return snapshot;
    });

    public PvpRatingReceipt Settle(AccountOwners.Lease owner, ushort characterId, Guid matchId,
        PvpRatingMode? expectedMode = null) => owner.Execute(() =>
    {
        var plan = Prepare(owner, characterId, matchId, expectedMode);
        return plan is null ? store.ReadPvpRatingClaim(owner, characterId, matchId)!.Receipt! : store.CommitPvpRating(owner, plan);
    });

    internal PvpRatingPlan? Prepare(AccountOwners.Lease owner, ushort characterId, Guid matchId,
        PvpRatingMode? expectedMode = null) => owner.Execute(() =>
    {
        var claim = store.ReadPvpRatingClaim(owner, characterId, matchId) ??
            throw new InvalidOperationException("决斗评分缺少本角色的持久结算来源");
        if (expectedMode is { } expected && claim.Mode != expected)
            throw new InvalidOperationException("决斗来源与所选评分模式不一致");
        if (claim.Receipt is not null) return null;
        var current = Capture(owner, characterId);
        var before = claim.Mode == PvpRatingMode.Matching
            ? claim.FrozenBefore ?? throw new InvalidDataException("匹配评分缺少冻结赛前快照") : current;
        if (before != current) throw new InvalidOperationException("匹配赛前评分快照与当前持久版本不符");
        var change = claim.Mode == PvpRatingMode.Total ? calculator.CalculateTotalCharacterExperience(before.State, claim.Outcome)
            : calculator.Calculate(before.State, claim.Mode, claim.Outcome, claim.Opponent);
        return new PvpRatingPlan(claim, before, change);
    });

    // 每次恢复有界；调用方在仍有待结算来源时不得开启下一局。
    internal bool RecoverBefore(AccountOwners.Lease owner, ushort characterId, Guid matchId) => owner.Execute(() =>
    {
        if (store.ReadPvpRatingClaim(owner, characterId, matchId)?.Receipt is not null) return true;
        foreach (var match in store.ReadPendingPvpRatings(owner, characterId, 32))
        {
            if (match == matchId) return true;
            Settle(owner, characterId, match);
        }
        return store.ReadPendingPvpRatings(owner, characterId, 1).FirstOrDefault() == matchId;
    });

    public bool Recover(AccountOwners.Lease owner, ushort characterId, Action<PvpRatingReceipt>? applied = null) => owner.Execute(() =>
    {
        foreach (var match in store.ReadPendingPvpRatings(owner, characterId, 32))
        {
            var receipt = Settle(owner, characterId, match);
            applied?.Invoke(receipt);
        }
        return store.ReadPendingPvpRatings(owner, characterId, 1).Length == 0;
    });
}
