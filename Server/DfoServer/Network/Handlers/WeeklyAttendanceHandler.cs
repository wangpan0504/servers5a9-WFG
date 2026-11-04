using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class WeeklyAttendanceHandler : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK, SignAsync, nameof(WeeklyAttendanceHandler));
        commands.Register(CmdPacketType.EVENT_REQUEST, BonusAsync, nameof(WeeklyAttendanceHandler));
    }
    static async Task<CommandResult> SignAsync(GameConnectionContext context, Packet request)
    {
        if (request.Kind != 1) return CommandResult.Handled;
        if (WeeklyAttendanceRequests.Sign(request.Body) && context.WeeklyAttendance is { } weekly)
            await weekly.UseAsync(true, false, true);
        else await context.Reply(CmdPacketType.EVENT_TOTAL_ATTENDANCE_CHECK_THISWEEK,
            WeeklyAttendancePackets.Ack(null), "weekly_attendance_invalid", () => context.Owner.IsCurrent);
        return CommandResult.Handled;
    }
    static async Task<CommandResult> BonusAsync(GameConnectionContext context, Packet request)
    {
        if (!WeeklyAttendanceRequests.OwnsBonus(request.Body)) return CommandResult.Unhandled;
        if (request.Kind == 1 && WeeklyAttendanceRequests.Bonus(request.Body) && context.WeeklyAttendance is { } weekly)
            await weekly.UseAsync(false, true, false);
        return CommandResult.Handled;
    }
}
