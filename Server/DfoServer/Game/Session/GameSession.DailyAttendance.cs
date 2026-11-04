using DfoServer.Game.Characters;
using DfoServer.Game.Events;

namespace DfoServer.Game.Session;

internal sealed record DailyAttendanceView(Character Character, long Epoch, DailyAttendanceSnapshot Snapshot, bool Enabled, long ActivityRevision, uint MailId);

internal sealed partial class GameSession
{
    public long DailyAttendanceVersion { get; private set; }

    public DailyAttendanceView? ReadDailyAttendance(DailyAttendanceService service, bool claim = false)
    {
        var capture = owner.Execute(() => CanAdvanceServiceDay() ? (Selected, SelectionEpoch) : default);
        if (capture.Selected is null) return null;
        var plan = claim ? service.Prepare(owner, claim: true) : null;
        return owner.Execute(() =>
        {
            if (!CanAdvanceServiceDay() || !ReferenceEquals(Selected, capture.Selected) || SelectionEpoch != capture.SelectionEpoch)
                return null;
            uint mail = plan is null ? 0 : service.Claim(owner, capture.Selected, plan).MailId;
            if (mail > 0)
            {
                DailyAttendanceVersion++;
                NotifyMailbox(capture.Selected.Id, restoreOnTown: true);
            }
            var state = service.Events.Read(ServerEventService.DailyAttendanceId);
            return new DailyAttendanceView(Selected!, SelectionEpoch, service.Read(owner), state.Active, state.Schedule.Revision, mail);
        });
    }

    public bool CanPublishDailyAttendance(DailyAttendanceView view, DailyAttendanceService service) => owner.IsCurrent && owner.Execute(() =>
        CanAdvanceServiceDay() && ReferenceEquals(Selected, view.Character) && SelectionEpoch == view.Epoch &&
        service.Events.IsDailyAttendanceCurrent(view.ActivityRevision, view.Enabled));
}
