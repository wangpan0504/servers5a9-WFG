using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal sealed record FormalDungeonReceipt(Character Character, long Generation, long SelectionEpoch,
    DungeonEntryPlan Entry, DungeonRunSnapshot Snapshot, DungeonMonsterPlan Monsters, uint MapSeed);
internal sealed record FormalDungeonDeathResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    ushort MonsterKey = 0, DungeonExperienceReward? Reward = null, DungeonClearFact? ClearFact = null,
    DungeonGroundObject? Drop = null, byte? PreviousLevel = null)
{
    public bool SpecialChildRetired { get; init; }
    public IReadOnlyList<ActiveMainQuest>? PreviousQuests { get; init; }
    public IReadOnlyList<InventorySlot> QuestDropSlots { get; init; } = [];
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
}
internal sealed record DungeonPickupCommit(Character Character, bool Duplicate, InventorySlot Destination, long ItemUid,
    bool ProjectionCurrent);
internal sealed record FormalDungeonPickupResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DungeonGroundObject? Ground = null, DungeonPickupCommit? Commit = null);
internal sealed record DungeonSettlementPresentation(uint ClearTimeMilliseconds, byte RankPoint, byte RankGrade,
    uint MonsterExperience);
internal sealed record FormalDungeonSettlementResult(DungeonRunOutcome Outcome, FormalDungeonReceipt? Receipt = null,
    DfoServer.GameWorld.Dungeons.DungeonClearExperienceReward? Reward = null, DungeonSettlementPresentation? Presentation = null,
    Character? PreviousCharacter = null);
