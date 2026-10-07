using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SessionHandler(TownProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EXIT, ExitAsync, nameof(SessionHandler));
        commands.Register(CmdPacketType.RETURN_SELECT_CHARACTER, ReturnAsync, nameof(SessionHandler));
    }

    async Task<CommandResult> ExitAsync(GameConnectionContext context, Packet request)
    {
        byte mode = SessionRequests.Exit(request.Body);
        byte[] ack = SessionPackets.Exit();
        context.Session.ReturnToRoster();
        context.Activity?.Reset();
        await projection.DetachAsync(context);
        log.Write("exit", new { Connection = context.Id, Mode = mode });
        // ACK 发完后才返回关闭结果，由协议层统一释放 owner 和发送泵。
        await context.Reply(CmdPacketType.EXIT, ack, "exit_ack");
        return CommandResult.CloseConnection;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        SessionRequests.Empty(request.Body);
        context.Session.ReturnToRoster();
        context.Activity?.Reset();
        await projection.DetachAsync(context);
        context.RosterRequested = true;
        context.SelectionRosterPushed = true;
        await context.Reply(CmdPacketType.RETURN_SELECT_CHARACTER, SessionPackets.ReturnToSelection(), "return_selection_ack");
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(context.Session.Roster()), "return_selection_roster");
        return CommandResult.Handled;
    }

}
