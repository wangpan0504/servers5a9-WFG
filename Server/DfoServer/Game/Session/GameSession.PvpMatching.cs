using DfoServer.Game.Pvp;
using DfoServer.Game.Social;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    PvpMatchingTicket? pvpMatching;
    internal PvpMatchingTicket? CurrentPvpMatching => Volatile.Read(ref pvpMatching);

    bool CanUsePvpMatchingActivities => Ready && Selected is not null &&
        ChannelEntry?.Type is 8 or 24 && pvpTraining is null && pvpPractice is null && CurrentParty?.Invoke() is null &&
        pendingRental is null && !HasPendingTownQuestCommit && !HasActiveDungeonContinuation &&
        !HasPendingQuestReplay && !HasPendingFormalCard;

    internal bool CanRemainPvpMatching => CanUsePvpMatchingActivities && Phase == SessionPhase.Town && pvpArena is null;

    internal PvpArenaAdmission? CapturePvpMatchingAdmission() => owner.Execute(() =>
        CanRemainPvpMatching && PublicCharacter(Selected!.Id) is { } identity
            ? new PvpArenaAdmission(new SocialActor(owner, this, Selected, SelectionEpoch), identity) : null);

    internal bool InstallPvpMatching(PvpMatchingTicket ticket) => owner.Execute(() =>
    {
        if (pvpMatching is not null || !CanRemainPvpMatching || !ticket.Actor.IsCurrent() ||
            !ReferenceEquals(ticket.Actor.Owner, owner) || !ReferenceEquals(ticket.Actor.Session, this)) return false;
        Volatile.Write(ref pvpMatching, ticket);
        return true;
    });

    internal bool RemovePvpMatching(PvpMatchingTicket expected) => owner.Execute(() =>
    {
        if (!ReferenceEquals(pvpMatching, expected)) return false;
        Volatile.Write(ref pvpMatching, null);
        return true;
    });

    // 调用方持双方账号锁：先对两人都检查，再提交，期间不插入捕获资料或外部回调。
    internal bool CanTransferPvpMatching(PvpMatchingTicket ticket) =>
        ReferenceEquals(pvpMatching, ticket) && CanRemainPvpMatching && ticket.Actor.IsCurrent() &&
        ReferenceEquals(ticket.Actor.Owner, owner) && ReferenceEquals(ticket.Actor.Session, this);

    internal void TransferPvpMatching(PvpArenaMembership member)
    {
        Volatile.Write(ref pvpArena, member);
        pvpArenaVersion++;
        SetState(SessionPhase.PvpRoom);
    }
}
