using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ClientNoticeHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        foreach (var opcode in new[]
        {
            CmdPacketType.USE_SKILL,
            CmdPacketType.SECURITY_STATUS,
            CmdPacketType.FPS_PERFORMANCE_STATISTIC
        })
            commands.Register(opcode, NoticeAsync, nameof(ClientNoticeHandler));
    }

    Task<CommandResult> NoticeAsync(GameConnectionContext context, Packet request)
    {
        if (!ClientNotices.TryParse(request.Opcode, request.Body, out var notice)) return Task.FromResult(CommandResult.Unhandled);
        log.Write("client_notice", new { Connection = context.Id, request.Opcode, notice.Action, notice.Detail });
        return Task.FromResult(CommandResult.Handled);
    }
}
