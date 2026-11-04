using DfoServer.Game.Events;
using DfoServer.Infrastructure;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 仅缓存本连接已发布的版本；账号进度仍由Session及仓储持有，日界复用ServiceDayProjection。
internal sealed class DailyAttendanceProjection(GameConnectionContext context, DailyAttendanceService service, TimeProvider time, ServerLog log)
{
    (long Epoch, int Day, long Version)? published;
    public bool NeedsRefresh { get; private set; } = true;
    public bool Enabled => service.Events.Read(ServerEventService.DailyAttendanceId).Active;
    public void Invalidate() => NeedsRefresh = true;

    public async Task RefreshAsync(bool claim = false)
    {
        var key = (context.Session.SelectionEpoch, PcRoomTimePointDay.At(time.GetUtcNow()), context.Session.DailyAttendanceVersion);
        if (!claim && !NeedsRefresh && published == key) return;
        var view = context.Session.ReadDailyAttendance(service, claim);
        if (view is null) return;
        if (view.MailId > 0)
            log.Write("daily_attendance_claimed", new { Connection = context.Id, Character = view.Character.Id, view.Snapshot.Total, view.MailId });
        bool Guard() => context.Session.CanPublishDailyAttendance(view, service);
        if (claim)
            await context.Reply(CmdPacketType.AT_DAILY_ATTENDANCE, [], "daily_attendance_claim", Guard);
        if (view.Enabled)
            await context.Notify(NotiPacketType.INTEGRATE_EVENT_DATA, DailyAttendancePackets.State(view.Snapshot, service.Catalog),
                "daily_attendance_state", Guard);
        if (!Guard()) return;
        published = (view.Epoch, view.Snapshot.Day, context.Session.DailyAttendanceVersion);
        NeedsRefresh = false;
    }
}
