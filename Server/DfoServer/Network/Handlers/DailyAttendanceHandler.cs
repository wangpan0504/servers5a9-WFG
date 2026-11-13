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
        // 01313C1A..01313C3D调03BDF610写入13B帧头后再经03BDF2C0追加13B栈缓冲作为opaque正文；兼容合成空正文与原生13B正文。
        if (request.Body.Length is not (0 or 13))
        {
            log.Write("request_rejected", new { Connection = context.Id, Action = "daily_attendance", Reason = "invalid_length", request.Body.Length });
            return CommandResult.Handled;
        }
        if (context.DailyAttendance is { } attendance) await attendance.RefreshAsync(claim: true);
        return CommandResult.Handled;
    }
}
