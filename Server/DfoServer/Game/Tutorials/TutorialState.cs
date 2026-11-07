using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Tutorials;

internal sealed record CharacterTutorialState(uint DungeonId, bool Completed = false, bool RewardReceived = false)
{
    public void Validate()
    {
        if (DungeonId is 0 or > int.MaxValue) throw new InvalidDataException("训练状态副本编号无效");
    }
}
internal sealed record TutorialCheckpointPlan(Character Expected, Guid EventId, DungeonRunIdentity? Identity,
    int MapId, string Kind, uint Subject, uint Experience, InventoryPlan Inventory,
    CharacterTutorialState After, Location Position, CharacterGrowthPlan? Growth = null)
{
    public PreparedDungeonDeath? Death { get; init; }
    public DungeonExperienceReward? DeathReward { get; init; }
    public FrozenDungeonGroundDeath? Loot { get; init; }
}
internal sealed record TutorialCheckpointResult(Character Character, bool Duplicate)
{
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
}
internal readonly record struct TutorialRewardRow(ushort Slot, uint ItemId, uint Count);
internal sealed record TutorialFlagResult(ushort Error, Character Character, long Generation, long SelectionEpoch,
    IReadOnlyList<TutorialRewardRow> Rewards, InventoryResult? Inventory = null)
{
    public DfoServer.Game.Session.SessionPhase Phase { get; init; }
    public DungeonRunIdentity? Identity { get; init; }
}
