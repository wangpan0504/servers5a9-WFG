using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class WeeklyAttendanceProjection(GameConnectionContext context, WeeklyAttendanceService service,
    TownProjection town, ServerLog log)
{
    byte[]? previous;
    long epoch = -1;
    DateTimeOffset next;
    bool? published;
    public bool Enabled => service.Enabled;
    public void EventsPublished(bool enabled) => published = enabled;
    public async Task RefreshAsync(bool force = false)
    {
        if (!force && epoch == context.Session.SelectionEpoch && service.Now < next) return;
        await UseAsync(false, false, false);
    }
    public async Task UseAsync(bool sign, bool bonus, bool acknowledge)
    {
        WeeklyAttendanceUse? use = null;
        try { use = context.Session.UseWeeklyAttendance(service, sign, bonus); }
        catch (WeeklyAttendanceRejectedException error)
        {
            log.Write("weekly_attendance_rejected", new { Connection = context.Id, Error = error.Message });
        }
        bool Guard() => use is not null && context.Session.CanPublishWeeklyAttendance(use);
        if (acknowledge)
            await context.Reply(CmdPacketType.EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK,
                WeeklyAttendancePackets.Ack(use?.Commit), "weekly_attendance_sign", () => context.Owner.IsCurrent &&
                    (use is null || Guard()));
        if (use is null) return;
        next = service.Now.AddSeconds(30);
        bool enabled = Enabled;
        if (published != enabled) await town.ServerEventsAsync(context, Guard);
        if (use.Commit is { } committed)
        {
            var body = WeeklyAttendancePackets.State(committed.Snapshot);
            if (epoch != use.Epoch || previous is null || !body.AsSpan().SequenceEqual(previous))
                await context.Notify(NotiPacketType.EVENT_TOTAL_ATTENDANCE, body, "weekly_attendance_state", Guard);
            if (Guard()) { previous = body; epoch = use.Epoch; }
            if (!committed.MailIds.IsEmpty)
            {
                await context.Notify(NotiPacketType.MAILBOX_ALARM, [1, 0], "weekly_attendance_mail", Guard);
                log.Write("weekly_attendance_claimed", new { Connection = context.Id, Character = use.CharacterId,
                    Count = committed.MailIds.Length, Bonus = bonus });
            }
        }
        else if (epoch != use.Epoch || previous is not null)
        {
            previous = null;
            epoch = use.Epoch;
        }
    }
}
