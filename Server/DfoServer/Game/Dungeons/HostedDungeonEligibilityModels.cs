using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record HostedDungeonEligibilityOptions
{
    public bool Enabled { get; init; }
    public string? HostFileName { get; init; }
    public string[] HostArguments { get; init; } = [];
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int OutputBudgetBytes { get; init; } = 64 * 1024;
}

internal sealed record HostedDungeonEligibilityResult(bool CanFight, string Reason, Guid ProbeId, string? ProfileSchema = null);

internal sealed record HostedDungeonEligibilityCapture(
    Guid ProbeId,
    long Generation,
    long SelectionEpoch,
    ushort CharacterId,
    long CharacterRevision,
    DungeonRunIdentity Identity,
    uint DungeonId,
    uint QuestId,
    int MapId,
    DungeonPoint MapPosition,
    ushort[] LivingKeys,
    HostedDungeonEligibilityWireSpawn[] Spawns,
    string? RejectReason);

internal sealed record HostedDungeonEligibilityWireIdentity(
    ushort CharacterId, uint QuestId, long TaskAttempt, Guid InstanceId, long Epoch);

internal sealed record HostedDungeonEligibilityWireSpawn(
    ushort ObjectKey, int MonsterId, int SpawnLevel, int BattleLevel);

internal sealed record HostedDungeonEligibilityWireRequest(
    Guid ProbeId,
    long OwnerGeneration,
    long SelectionEpoch,
    long CharacterRevision,
    ushort CharacterId,
    HostedDungeonEligibilityWireIdentity Identity,
    uint DungeonId,
    uint QuestId,
    int MapX,
    int MapY,
    int MapId,
    HostedDungeonEligibilityWireSpawn[] Spawns);

internal sealed record HostedDungeonEligibilityWireReply(
    Guid ProbeId,
    bool CanFight,
    string? Reason,
    string? ProfileSchema);
