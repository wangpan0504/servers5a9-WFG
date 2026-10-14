using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal sealed record FormalDungeonReceipt(Character Character, long Generation, long SelectionEpoch,
    DungeonEntryPlan Entry, DungeonRunSnapshot Snapshot, DungeonMonsterPlan Monsters, uint MapSeed);
internal sealed record FormalDungeonDeathResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    ushort MonsterKey = 0, DungeonExperienceReward? Reward = null, DungeonClearFact? ClearFact = null,
    DungeonGroundObject? Drop = null, byte? PreviousLevel = null)
{
    public ImmutableArray<DungeonGroundObject> Drops { get; init; } = [];
    public uint GainedExperience { get; init; }
    public DungeonPlannedMonster? ReportedTarget { get; init; }
    public bool SpecialChildRetired { get; init; }
    public DungeonElevatorReceipt? Elevator { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
    public IReadOnlyList<InventorySlot> QuestDropSlots { get; init; } = [];
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
}
internal sealed record DungeonPickupCommit(Character Character, bool Duplicate, InventorySlot Destination, long ItemUid,
    bool ProjectionCurrent, CurrencyKind? Currency = null, int Amount = 0);
internal sealed record FormalDungeonPickupResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DungeonGroundObject? Ground = null, DungeonPickupCommit? Commit = null)
{
    public bool CapacityRejected { get; init; }
}
internal sealed record DungeonSettlementPresentation(uint ClearTimeMilliseconds, byte RankPoint, byte RankGrade,
    uint MonsterExperience)
{
    public uint MonsterGrowthContractExperience { get; init; }
}
internal sealed record FormalDungeonSettlementResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DfoServer.GameWorld.Dungeons.DungeonClearExperienceReward? Reward = null, DungeonSettlementPresentation? Presentation = null,
    Character? PreviousCharacter = null)
{
    public uint GainedExperience { get; init; }
    public bool LuckyStarGained { get; init; }
    public int LuckyStarTotal { get; init; }
    public DungeonCardReward? Card { get; init; }
    public InventoryResult? Inventory { get; init; }
    public uint? MailId { get; init; }
    public DungeonCardOptions? Options { get; init; }
    public DungeonCardReward? BlackDiamond { get; init; }
}
