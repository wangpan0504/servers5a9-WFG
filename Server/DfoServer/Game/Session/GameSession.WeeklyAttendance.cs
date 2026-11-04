using DfoServer.Game.Events;

namespace DfoServer.Game.Session;

internal sealed record WeeklyAttendanceUse(long Epoch, ushort CharacterId, WeeklyAttendanceCommit? Commit);

internal sealed partial class GameSession
{
    public WeeklyAttendanceUse? UseWeeklyAttendance(WeeklyAttendanceService service, bool claim = false, bool bonus = false)
    {
        var capture = owner.Execute(() => Ready && Selected is not null && Phase is not (SessionPhase.Unselected or SessionPhase.Loading)
            ? (Selected, SelectionEpoch) : default);
        if (capture.Selected is null) return null;
        var plan = service.Prepare(owner, capture.Selected, claim, bonus);
        return owner.Execute<WeeklyAttendanceUse?>(() =>
        {
            if (!Ready || !ReferenceEquals(Selected, capture.Selected) || SelectionEpoch != capture.SelectionEpoch) return null;
            return new(SelectionEpoch, Selected.Id, plan is null ? null : service.Commit(owner, Selected, plan));
        });
    }

    public bool CanPublishWeeklyAttendance(WeeklyAttendanceUse use) => owner.IsCurrent && owner.Execute(() =>
        Ready && SelectionEpoch == use.Epoch && Selected?.Id == use.CharacterId);
}
