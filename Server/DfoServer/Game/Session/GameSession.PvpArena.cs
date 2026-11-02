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

    // 在本账号owner内只读捕获评分；公开快照携带结果，广播不再访问其他账号或仓储。
    internal PvpDisplayGrade ReadPvpGrade(DfoServer.Game.Characters.Character character) => owner.Execute(() =>
    {
        if (Selected?.Id != character.Id) throw new InvalidOperationException("Stale pvp grade character");
        if (pvpRatings is null) return default;
        var rating = pvpRatings.Capture(owner, character.Id);
        return new PvpDisplayGrade(checked((byte)pvpRatings.Grade(rating)), rating.State.PowerTitle);
    });

    internal bool RecoverPvpRatings(PvpRatingSettlement ratings) => owner.Execute(() =>
        Ready && Selected is { } character && ratings.Recover(owner, character.Id, InstallPvpQuestAdvance));

    internal void InstallPvpQuestAdvance(PvpRatingReceipt receipt) => owner.Execute(() =>
    {
        if (receipt.QuestAdvance is not { } progress || Selected is not { } character ||
            receipt.Account != owner.Account || receipt.CharacterId != character.Id) return true;
        Selected = ApplyPvpQuestAdvance(character, progress);
        return true;
    });

    static DfoServer.Game.Characters.Character ApplyPvpQuestAdvance(DfoServer.Game.Characters.Character character, PvpQuestAdvance progress)
    {
        if (character.Revision == progress.Revision && character.ActiveMainQuests.SequenceEqual(progress.After) || character.Revision > progress.Revision) return character;
        if (character.Revision != progress.BeforeRevision || !character.ActiveMainQuests.SequenceEqual(progress.Before))
            throw new InvalidOperationException("决斗任务回执与本角色快照不一致");
        return character with { Revision = progress.Revision, ActiveMainQuests = progress.After.ToArray() };
    }
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
