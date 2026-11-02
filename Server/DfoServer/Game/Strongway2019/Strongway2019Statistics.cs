using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Strongway2019;

internal sealed record Strongway2019Statistics(uint MatchingWins, uint MatchingBestElapsedMilliseconds,
    uint NormalBestMode, uint NormalBestElapsedMilliseconds);

internal sealed record Strongway2019StatisticsPlan(Character Expected, SinglePlayerDungeonRun Run, DungeonClearFact? Fact)
{
    public DungeonRunIdentity Identity { get; init; } = Fact?.Identity ?? Run.Scene.Identity;
    public Strongway2019Competition? Competition { get; init; }
    public Strongway2019Terminal? Terminal { get; init; }
}

internal sealed record Strongway2019StatisticsCommit(bool Recorded, bool Duplicate);

internal interface IStrongway2019StatisticsStore
{
    Strongway2019Statistics ReadStrongway2019Statistics(AccountOwners.Lease owner, ushort characterId);
    Strongway2019StatisticsCommit CommitStrongway2019Statistics(AccountOwners.Lease owner, Strongway2019StatisticsPlan plan);
}
