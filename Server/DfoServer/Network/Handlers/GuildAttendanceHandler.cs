using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildAttendanceHandler(GuildAttendanceService service) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.TODAY_GUILD_ATTENDANCE_DETAIL_INFO, ReadAsync, nameof(GuildAttendanceHandler));

    async Task<CommandResult> ReadAsync(GameConnectionContext context, Packet request)
    {
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        var view = request.Body.Length == 0 ? service.Read(actor) : null;
        await context.Reply(CmdPacketType.TODAY_GUILD_ATTENDANCE_DETAIL_INFO,
            view is null ? GuildAttendancePackets.Failure(34) : GuildAttendancePackets.Names(view),
            "guild_attendance_names", actor.IsCurrent);
        return CommandResult.Handled;
    }
}
