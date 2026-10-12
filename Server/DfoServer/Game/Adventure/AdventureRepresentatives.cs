using System.Collections.Immutable;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Adventure;

internal readonly record struct AdventureSelection(byte Job, byte Grow, ushort? CharacterId);
internal sealed record AdventureRepresentativePlan(AdventureSnapshot Before, ImmutableArray<AdventureSelection> After, ushort ActorId)
{
    public bool Changed => !Before.State.Representatives.SequenceEqual(After);
}
internal sealed record AdventureRepresentativeResult(bool Accepted, ushort Error, AdventureSnapshot? Snapshot = null)
{
    public Character? Selected { get; init; }
    public long Generation { get; init; }
    public long Epoch { get; init; }
    public DfoServer.Game.Session.SessionPhase Phase { get; init; }
    public DfoServer.Game.Dungeons.DungeonRunIdentity? Dungeon { get; init; }
}
