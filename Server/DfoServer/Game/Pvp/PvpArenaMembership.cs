using DfoServer.Game.Session;
using DfoServer.Game.Social;

namespace DfoServer.Game.Pvp;

internal sealed record PvpArenaAdmission(SocialActor Actor, PublicCharacterSnapshot Identity);
internal sealed record PvpArenaMembership(PvpArenaRoom Room, Guid Token, SocialActor Actor,
    PublicCharacterSnapshot Identity)
{
    public bool IsCurrent => Actor.IsCurrent() && Actor.Session.Phase == SessionPhase.PvpRoom &&
        ReferenceEquals(Actor.Session.CurrentPvpArena, this);
}

internal sealed record PvpArenaDeparture(PvpArenaMembership Member, long Version);
internal enum PvpArenaNoticeKind { Room, Seats, Ready, State, Start, Loaded, Connected, Death, Endpoints, MatchingCancelled, Matching, ResultReturned, TotalSelected, TotalProgress }
internal sealed record PvpArenaNotice(PvpArenaMembership Recipient, PvpArenaRoomView View,
    PvpArenaDeparture? Departure = null)
{
    public PvpArenaNoticeKind Kind { get; init; } = PvpArenaNoticeKind.Room;
    public byte ReadySeat { get; init; }
    public ushort Peer { get; init; }
    public byte VictimSeat { get; init; }
    public byte KillerSeat { get; init; } = byte.MaxValue;
    public uint MatchingId { get; init; }
    public long TotalRevision { get; init; }
    public System.Collections.Immutable.ImmutableArray<PvpTotalRoundChoice> TotalChoices { get; init; } = [];
    public System.Collections.Immutable.ImmutableArray<PvpTotalBattleRoster> TotalRosters { get; init; } = [];
    public System.Collections.Immutable.ImmutableDictionary<ushort, PvpRatingSnapshot> MatchingRatings { get; init; } =
        System.Collections.Immutable.ImmutableDictionary<ushort, PvpRatingSnapshot>.Empty;
    public System.Collections.Immutable.ImmutableArray<DfoServer.Game.Party.PartyEndpointRow> Endpoints { get; init; } = [];
    public System.Collections.Immutable.ImmutableArray<PublicCharacterSnapshot> Characters { get; init; } = [];
}
