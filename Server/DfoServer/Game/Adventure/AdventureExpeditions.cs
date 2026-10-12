using System.Collections.Immutable;

namespace DfoServer.Game.Adventure;

internal sealed record ExpeditionMember(ushort Id, byte Selector, string Name, byte Job, byte Grow, byte Level);
internal sealed record AdventureExpedition(byte Area, Guid Id, long Started, long Ends, uint Reward, uint ExtraReward,
    ImmutableArray<byte> Attributes, ImmutableArray<ushort> Members)
{
    public ImmutableArray<ExpeditionMember> FrozenMembers { get; init; } = [];
}
internal sealed record AdventureExpeditionPlan(AdventureSnapshot Before, AdventureState After, string Action, ushort ActorId = 0);
