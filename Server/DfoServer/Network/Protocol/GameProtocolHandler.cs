using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Protocol;

internal sealed class GameProtocolHandler(ServerOptions options, LoginHandler loginHandler, GameCommandRegistry commands, ICharacterRepository repository, TownCatalog towns, OnlineSessionDirectory online, TownProjection projection, ServerLog log,
    Func<GameSession, IGameConnectionActivity>? createActivity = null, MailboxProjection? mailbox = null, DfoServer.GameWorld.Quests.QuestCatalog? quests = null) : IConnectionProtocol
{
    public async Task RunAsync(ConnectionContext connection)
    {
        using var login = await loginHandler.AuthenticateAsync(connection);
        if (login is null)
            return;
        await using var sender = new GameConnectionSender(login.Owner, connection.Stream, connection.Lifetime, login.Sequence, (kind, opcode, sequence, body, action) => log.Write("send", new { Connection = connection.Id, Kind = kind, Opcode = opcode, Name = kind == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString(), Sequence = sequence, Action = action, Body = Convert.ToHexString(body.Span) }), connection.CaptureSent);
        var session = new GameSession(repository, towns, login.Owner, login.Identity, quests);
        var context = new GameConnectionContext(connection, login.Owner, session, sender, createActivity?.Invoke(session));
        var elevator = new DungeonElevatorProjection(context, log);
        context.Elevator = elevator;
        if (mailbox is not null)
            session.MailboxArrived = receipt => mailbox.Alarm(context, receipt);
        try
        {
            online.Register(login.Owner, session);
            await context.Reply(CmdPacketType.LOGIN, ChannelPackets.Endpoint(options.AdvertisedAddress, options.GamePort), "endpoint");
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(session.Roster()), "roster");
            while (!connection.Stop.IsCancellationRequested)
            {
                var request = GameFrame.ParseClient(await connection.ReadAsync());
                bool loginPacket = request.Opcode is (ushort)CmdPacketType.LOGIN or (ushort)CmdPacketType.LOGIN_PRECHECK;
                if (loginPacket)
                    throw new InvalidDataException("Login already completed on this connection");
                string name = ((CmdPacketType)request.Opcode).ToString();
                if (session.AdvanceQuestCycle(DateTimeOffset.UtcNow) is { } cycle)
                {
                    bool Guard() => session.CanPublishQuestCycle(cycle);
                    await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(cycle.Character), "quest_daily_cycle", Guard);
                    if (cycle.Phase == SessionPhase.Town && quests is not null)
                        await new QuestProjection(quests).RefreshAsync(context, cycle.Character, Guard);
                }
                string body = PacketCapture.IsAuthentication(request.Opcode) ? "[login-redacted]" : Convert.ToHexString(request.Body);
                log.Write("recv", new { Connection = connection.Id, request.Kind, request.Opcode, Name = name, Body = body });
                var result = await commands.DispatchAsync(context, request);
                if (result == CommandResult.CloseConnection)
                    return;
                if (result == CommandResult.Unhandled)
                    log.Write("unimplemented", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind,
                        request.Opcode, Name = name, Length = request.Body.Length, Body = body });
            }
        }
        finally
        {
            await elevator.DisposeAsync();
            online.Remove(login.Owner.Account, session);
            projection.Remove(context);
            context.Activity?.Reset();
            session.End();
            // 随后的 await using 先停止发送泵，再由 login 释放精确 owner。
        }
    }
}
