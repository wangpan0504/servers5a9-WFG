using DfoServer.Game.Characters;

namespace DfoServer.Game.Adventure;

internal sealed record AdventureExpeditionResult(bool Accepted, ushort Error, AdventureSnapshot? Snapshot = null, uint Reward = 0)
{
    public Character? Selected { get; init; }
    public long Generation { get; init; }
    public long Epoch { get; init; }
    public DfoServer.Game.Session.SessionPhase Phase { get; init; }
    public DfoServer.Game.Dungeons.DungeonRunIdentity? Dungeon { get; init; }
}
