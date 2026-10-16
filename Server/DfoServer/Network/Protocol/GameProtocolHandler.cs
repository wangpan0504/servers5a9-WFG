using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Protocol;

internal sealed class GameProtocolHandler(ServerOptions options, LoginHandler loginHandler, GameCommandRegistry commands, ICharacterRepository repository, TownCatalog towns, OnlineSessionDirectory online, TownProjection projection, ServerLog log,
    Func<GameSession, IGameConnectionActivity>? createActivity = null, MailboxProjection? mailbox = null,
    DfoServer.GameWorld.Quests.QuestCatalog? quests = null, GameDebugSettings? debugSettings = null,
    AdventureProjection? adventure = null, DfoServer.Game.Quests.QuestSkipEffects? questEffects=null,
    DfoServer.GameWorld.Items.PremiumContractCatalog? contracts = null, CharacterStatComputer? stats = null,
    DfoServer.Network.Projection.Inventory.InventoryProjection? inventory = null, TimeProvider? time = null,
    GuildProjection? guild = null,
    DfoServer.Game.Dungeons.DungeonPassiveDropPolicy? passiveDrops = null) : IConnectionProtocol
{
    public async Task RunAsync(ConnectionContext connection)
    {
        using var login = await loginHandler.AuthenticateAsync(connection);
        if (login is null)
            return;
        await using var sender = new GameConnectionSender(login.Owner, connection.Stream, connection.Lifetime, login.Sequence, (kind, opcode, sequence, body, action) => log.Write("send", new { Connection = connection.Id, Kind = kind, Opcode = opcode, Name = kind == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString(), Sequence = sequence, Action = action, Body = Convert.ToHexString(body.Span) }), connection.CaptureSent);
        var session = new GameSession(repository, towns, login.Owner, login.Identity, quests, debugSettings, login.Channel.Entry,questEffects, contracts,
            passiveDrops: passiveDrops);
        var context = new GameConnectionContext(connection, login.Owner, session, sender, createActivity?.Invoke(session));
        CubeContractConsumptionProjection? cubeConsumption = null;
        if (contracts is not null)
        {
            var cubeService = new DfoServer.Game.Inventory.Services.CubeContractService(contracts);
            context.Premiums = new(context, cubeService, stats ?? throw new InvalidOperationException("契约缺少属性投影"), time);
            cubeConsumption = new(context, cubeService, inventory ?? throw new InvalidOperationException("契约缺少库存投影"), log, time);
        }
        var elevator = new DungeonElevatorProjection(context, log);
        context.Elevator = elevator;
        var death = new DungeonDeathProjection(context, projection, log);
        context.Death = death;
        if (mailbox is not null)
            session.MailboxArrived = receipt => mailbox.Alarm(context, receipt);
        if (guild is not null)
        {
            // 建会认证的两个跨连接入口都绑定本条连接：邀请/取消推给被邀请方，建会结果（金币差额 + 3 个通知）发布给创建者。
            session.GuildPermitNoticeArrived = notice => guild.Notice(context, notice);
            session.GuildPermitResolved = outcome => guild.ResolvedAsync(context, outcome);
        }
        try
        {
            online.Register(login.Owner, session);
            adventure?.Prepare(context);
            await context.Reply(CmdPacketType.LOGIN, ChannelPackets.Endpoint(options.AdvertisedAddress, login.Channel.Port), "endpoint");
            var roster = session.PrepareRoster();
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray()), "roster",
                () => session.CanPublishRoster(roster));
            session.MarkRosterPublished(roster);
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
                await context.CommandGate.WaitAsync(connection.Stop);
                try
                {
                    if (context.Premiums is { } premiums) await premiums.RefreshAsync();
                    if (cubeConsumption is not null) await cubeConsumption.RefreshAsync();
                }
                finally { context.CommandGate.Release(); }
                if (result == CommandResult.Unhandled)
                    log.Write("unimplemented", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind,
                        request.Opcode, Name = name, Length = request.Body.Length, Body = body });
            }
        }
        finally
        {
            if (cubeConsumption is not null) await cubeConsumption.DisposeAsync();
            await death.DisposeAsync();
            if (context.Cards is not null) await context.Cards.DisposeAsync();
            await elevator.DisposeAsync();
            online.Remove(login.Owner.Account, session);
            projection.Remove(context);
            context.Activity?.Reset();
            session.End();
            // 随后的 await using 先停止发送泵，再由 login 释放精确 owner。
        }
    }
}
