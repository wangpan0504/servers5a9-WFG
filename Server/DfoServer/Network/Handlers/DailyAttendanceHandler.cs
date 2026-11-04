using DfoServer.Infrastructure;
using DfoServer.Game.Session;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class DailyAttendanceHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.AT_DAILY_ATTENDANCE,
        HandleAsync, nameof(DailyAttendanceHandler), UserStateRequirement.WorldReady);

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet request)
    {
        // 01313C1A..01313C3D只封装13B帧，没有请求正文。
        if (request.Body.Length != 0)
        {
            log.Write("request_rejected", new { Connection = context.Id, Action = "daily_attendance", Reason = "invalid_length", request.Body.Length });
            return CommandResult.Handled;
        }
        if (context.DailyAttendance is { } attendance) await attendance.RefreshAsync(claim: true);
        return CommandResult.Handled;
    }
}
