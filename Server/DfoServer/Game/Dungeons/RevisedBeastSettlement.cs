using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using System.Collections.Immutable;
namespace DfoServer.Game.Dungeons;

internal interface IRevisedBeastRewardStore
{
    DungeonIncountUsage LoadRevisedBeastRewardUsage(AccountOwners.Lease owner, ushort characterId, DungeonIncountPeriod period);
    RevisedBeastRewardCommit CommitRevisedBeastStageReward(AccountOwners.Lease owner, SinglePlayerDungeonRun run, RevisedBeastStageRewardPlan plan);
    RevisedBeastRewardCommit CommitRevisedBeastReward(AccountOwners.Lease owner, SinglePlayerDungeonRun run, RevisedBeastRewardPlan plan);
}
internal sealed record RevisedBeastCounts(uint DailyEntriesUsed, uint WeeklyEntriesUsed, byte DailyRewards, byte WeeklyRewards);
internal sealed record RevisedBeastCountsReceipt(Character Character, long Generation, long SelectionEpoch,
    DungeonIncountPeriod Period, RevisedBeastCounts Counts);
internal sealed record RevisedBeastRewardPlan(Character Expected, RevisedBeastTerminal Terminal, uint DungeonId, byte Difficulty,
    InventoryPlan Inventory, ImmutableArray<InventoryReward> Rewards, DungeonIncountPeriod Period,
    int DailyLimit, int WeeklyLimit);
internal sealed record RevisedBeastRewardCommit(Character Character, bool Duplicate, bool ProjectionCurrent, bool Awarded,
    ImmutableArray<InventoryReward> Rewards, ImmutableArray<InventorySlot> ChangedSlots, bool Mailed);
internal sealed record RevisedBeastSettlementReceipt(FormalDungeonReceipt Scene, RevisedBeastTerminal Terminal, RevisedBeastRewardCommit Commit)
{
    // 展示包括已在阶段到账的奖励；不能把展示列表重新提交为库存增量。
    public ImmutableArray<InventoryReward> DisplayRewards { get; init; } = [];
    public uint ElapsedSeconds { get; init; }
}

internal sealed record RevisedBeastStage(DungeonRunIdentity Identity, int MonsterId);
internal sealed record RevisedBeastStageRewardPlan(Character Expected, RevisedBeastStage Stage,
    InventoryPlan Inventory, ImmutableArray<InventoryReward> Rewards, DungeonIncountPeriod Period,
    int DailyLimit, int WeeklyLimit);
internal sealed record RevisedBeastStageReceipt(FormalDungeonReceipt Scene, RevisedBeastStage Stage,
    RevisedBeastRewardCommit Commit);
