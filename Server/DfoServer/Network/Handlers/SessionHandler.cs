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
        if (context.Strongway2019 is not null) await context.Strongway2019.BeforeDepartureAsync(context);
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
        await projection.ReturnToSelectionAsync(context);
        return CommandResult.Handled;
    }

}
