using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

// 失败结算保留真实的已完成回合和死亡事实，不冒充普通通关ClearFact。
internal sealed record DungeonAltarCompletionFact(DungeonRunIdentity Identity, uint DungeonId, int CompletedRounds,
    int MaximumRounds, uint PlayMilliseconds, bool Cleared, ImmutableArray<Guid> DeathEvents);

internal sealed class PreparedDungeonAltarSettlement(DungeonAltarCompletionFact fact, DungeonAltarRewardPlan reward,
    InventoryPlan inventory, Character expected)
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DungeonAltarCompletionFact Fact { get; } = fact;
    public DungeonAltarRewardPlan Reward { get; } = reward;
    public InventoryPlan Inventory { get; } = inventory;
    public Character Expected { get; } = expected;
}

internal sealed record DungeonAltarSettlementCommit(Character Character, bool Duplicate, uint GainedExperience,
    uint ProficiencyGained, InventoryPlan Inventory, uint BestMilliseconds, int BestRounds)
{
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
}
