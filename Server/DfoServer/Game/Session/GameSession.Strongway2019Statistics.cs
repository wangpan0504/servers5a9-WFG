using DfoServer.Game.Strongway2019;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal Strongway2019StatisticsCommit? CommitStrongway2019Statistics(
        Strongway2019RewardPreparation expected, IStrongway2019StatisticsStore store) => owner.Execute(() =>
    {
        if (!CurrentStrongway2019Reward(expected)) return null;
        return store.CommitStrongway2019Statistics(owner,
            new(expected.Character, expected.Run, expected.Fact)
            { Identity = expected.Identity, Competition = expected.Competition, Terminal = expected.Terminal });
    });

    internal Strongway2019Statistics? ReadStrongway2019Statistics(IStrongway2019StatisticsStore store) => owner.Execute(() =>
        Selected is { } character ? store.ReadStrongway2019Statistics(owner, character.Id) : null);

    internal (Strongway2019RewardState Challenge, Strongway2019RewardState Matching)? ReadStrongway2019AccountRewards(
        IStrongway2019RewardStore store) => owner.Execute<(Strongway2019RewardState, Strongway2019RewardState)?>(() =>
    {
        if (Selected is null) return null;
        // 两类额度在同一账号owner内读取，周期只采样一次，不让日周切分裂账号快照。
        var period = Strongway2019RewardPeriod.At((timeProvider ?? TimeProvider.System).GetUtcNow());
        return (store.ReadStrongway2019RewardState(owner, period, Strongway2019RewardBucket.Challenge),
            store.ReadStrongway2019RewardState(owner, period, Strongway2019RewardBucket.Matching));
    });
}
