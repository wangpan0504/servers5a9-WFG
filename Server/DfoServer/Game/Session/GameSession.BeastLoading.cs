using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool HasPendingBeastLoading => formalDungeon?.BeastReservation is not null;

    internal int BeastServiceDay => BeastDungeonRotation.ServiceDay(IncountNow());
    internal TimeProvider BeastClock => timeProvider ?? TimeProvider.System;

    internal BeastDungeonLoadingReservation? PrepareBeastDungeonLoading(TimeProvider? partyClock = null) => owner.Execute(() =>
    {
        if (!Ready || Selected is not { } character || Phase != SessionPhase.Loading ||
            formalDungeon is not { BeastReservation: { } admission } dungeon ||
            admission.Day != BeastDungeonRotation.ServiceDay((partyClock ?? BeastClock).GetUtcNow()) ||
            !CanUseNativeBeastLocation() || dungeon.Run.Snapshot.Identity.TaskAttempt != character.DungeonState?.LastAttempt)
            return null;
        return new BeastDungeonLoadingReservation(owner, character, dungeon.Run.Snapshot.Identity,
            checked((int)dungeon.Entry.DungeonId), admission, partyClock ?? BeastClock);
    });

    internal void InstallBeastDungeonLoading(BeastDungeonLoadingReservation reservation, Character committed)
    {
        if (!ReferenceEquals(Selected, reservation.Expected) || formalDungeon is not { } dungeon ||
            dungeon.Run.Snapshot.Identity != reservation.Identity || !ReferenceEquals(dungeon.BeastReservation, reservation.Admission))
            throw new InvalidOperationException("已提交魔兽首房的原实例身份变化");
        Selected = committed;
        dungeon.BeastLoadingPayment = reservation.Admission.Ticket;
        dungeon.BeastReservation = null;
    }

    void ReleaseBeastDungeonReservation()
    {
        if (formalDungeon is not { } dungeon || Selected is not { } character) return;
        dungeon.BeastProgress?.RemoveParticipant(character.Id, PartyDungeon is null ||
            !PartyDungeon.Members.Any(member => !ReferenceEquals(member.Actor.Session, this) && member.IsCurrent()));
        if (dungeon.BeastReservation is null) return;
        repository.CancelBeastDungeonReservation(owner.Account, character.Id, dungeon.Run.Snapshot.Identity.InstanceId);
        dungeon.BeastReservation = null;
    }
}
