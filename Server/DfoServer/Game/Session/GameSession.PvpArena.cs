using DfoServer.Game.Pvp;
using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    PvpArenaMembership? pvpArena;
    long pvpArenaVersion;
    internal PvpArenaMembership? CurrentPvpArena => Volatile.Read(ref pvpArena);
    public Action<PvpArenaNotice>? PvpArenaNoticeArrived { get; set; }
    public Func<PvpArenaResultDelivery, bool>? PvpArenaResultArrived { get; set; }

    internal bool RecoverPvpRatings(PvpRatingSettlement ratings) => owner.Execute(() =>
        Ready && Selected is { } character && ratings.Recover(owner, character.Id));
    internal bool RecoverPvpTotalRatings(PvpTotalRatingSettlement ratings) => owner.Execute(() =>
        Ready && Selected is not null && ratings.Recover(owner));

    bool CanEnterPvpArena => Ready && Phase == SessionPhase.Town && Selected is not null &&
        ChannelEntry?.Type is 4 or 5 or 13 or 24 && pvpTraining is null && pvpArena is null && pvpMatching is null && CurrentParty?.Invoke() is null &&
        pendingRental is null && !HasPendingTownQuestCommit && !HasActiveDungeonContinuation &&
        !HasPendingQuestReplay && !HasPendingFormalCard;

    internal PvpArenaAdmission? CapturePvpArenaAdmission(bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanEnterPvpArena || PublicCharacter(Selected!.Id) is not { } identity) return null;
        return new PvpArenaAdmission(new SocialActor(owner, this, Selected, SelectionEpoch), identity);
    });

    internal bool InstallPvpArena(PvpArenaMembership member) => owner.Execute(() =>
    {
        if (!ReferenceEquals(member.Actor.Owner, owner) || !ReferenceEquals(member.Actor.Session, this) ||
            !member.Actor.IsCurrent() || !ReferenceEquals(member.Actor.Character, Selected) || !CanEnterPvpArena ||
            member.Room.Member(member.Token)?.CharacterId != Selected!.Id) return false;
        Volatile.Write(ref pvpArena, member);
        pvpArenaVersion++;
        SetState(SessionPhase.PvpRoom);
        return true;
    });

    internal PvpArenaDeparture? LeavePvpArena(PvpArenaMembership expected) => owner.Execute(() =>
    {
        if (!ReferenceEquals(pvpArena, expected) || !expected.IsCurrent || expected.Room.Member(expected.Token) is not { } member) return null;
        SetState(SessionPhase.Town);
        return new PvpArenaDeparture(expected, ++pvpArenaVersion, member.Seat);
    });

    internal bool CanPublishPvpArenaDeparture(PvpArenaDeparture departure) =>
        departure.Member.Actor.IsCurrent() && ReferenceEquals(departure.Member.Actor.Session, this) &&
        departure.Version == pvpArenaVersion && pvpArena is null && pvpMatching is null && Phase == SessionPhase.Town;
}
