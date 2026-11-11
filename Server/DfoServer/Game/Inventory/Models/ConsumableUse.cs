namespace DfoServer.Game.Inventory.Models;

internal sealed record ConsumableUseRequest(InventorySlot Position, uint OpaqueCheck, int ItemId, int Argument);
internal enum ConsumableUseOutcome { Committed, Rejected, CloseConnection }
internal sealed record ConsumableUseGuard(DfoServer.Game.Characters.Character? Character, long Generation, long SelectionEpoch,
    DfoServer.Game.Session.SessionPhase Phase, DfoServer.Game.Dungeons.DungeonRunIdentity? DungeonIdentity);
internal sealed record ConsumableUseResult(ConsumableUseOutcome Outcome, string Reason, InventoryResult? Inventory = null,
    ConsumableUseGuard? Guard = null)
{
    public long? SourceUid { get; init; }
    public bool PreserveSource { get; init; }
    public bool NativeNoConsumption { get; init; }
    public System.Collections.Immutable.ImmutableArray<ConsumableStateUpdate> EffectUpdates { get; init; } = [];
    public int? TreasureHunterBonus { get; init; }
    public bool? TreasureHunterFromExj { get; init; }
    public int? DungeonEpicBuffId { get; init; }
    public int? BossDropPercent { get; init; }
    public System.Collections.Immutable.ImmutableArray<int> VariousBuffIds { get; init; } = [];
    public int TownGiftItem { get; init; }
    public DfoServer.Game.Characters.ExperienceConsumablePlan? Experience { get; init; }
    public DfoServer.Game.Characters.PermanentGrowthPlan? PermanentGrowth { get; init; }
    public DfoServer.Game.Characters.LegacyStatusConsumablePlan? LegacyStatus { get; init; }
    public DfoServer.Game.Characters.FatigueConsumablePlan? FatigueRecovery { get; init; }
}
