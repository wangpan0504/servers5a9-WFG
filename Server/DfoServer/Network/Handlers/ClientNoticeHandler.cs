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
            CmdPacketType.REQUEST_BLACKLIST,
            CmdPacketType.GUILD_ALL_MEMBER_LIST, CmdPacketType.MERCENARY_INFO, CmdPacketType.SECURITY_STATUS,
            CmdPacketType.FPS_PERFORMANCE_STATISTIC, CmdPacketType.SEARCH_GUILD_RANKING
        })
            commands.Register(opcode, NoticeAsync, nameof(ClientNoticeHandler));
    }

    async Task<CommandResult> NoticeAsync(GameConnectionContext context, Packet request)
    {
        if (!ClientNotices.TryParse(request.Opcode, request.Body, out var notice)) return CommandResult.Unhandled;
        log.Write("client_notice", new { Connection = context.Id, request.Opcode, notice.Action, notice.Detail });
        if (request.Opcode == (ushort)CmdPacketType.REQUEST_BLACKLIST && context.Session.Selected is not null)
            await context.Reply(CmdPacketType.REQUEST_BLACKLIST, ClientReplies.EmptyBlacklist, "blacklist_empty");
        return CommandResult.Handled;
    }
}
