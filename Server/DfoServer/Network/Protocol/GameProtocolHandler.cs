using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Network.Protocol;

internal sealed class GameProtocolHandler(ServerOptions options, LoginHandler loginHandler, GameCommandRegistry commands, ICharacterRepository repository, TownCatalog towns, OnlineSessionDirectory online, TownProjection projection, ServerLog log,
    Func<GameSession, IGameConnectionActivity>? createActivity = null, MailboxProjection? mailbox = null,
    DfoServer.GameWorld.Quests.QuestCatalog? quests = null, GameDebugSettings? debugSettings = null,
    AdventureProjection? adventure = null, DfoServer.Game.Quests.QuestSkipEffects? questEffects=null,
    DfoServer.GameWorld.Items.PremiumContractCatalog? contracts = null, CharacterStatComputer? stats = null,
    DfoServer.Network.Projection.Inventory.InventoryProjection? inventory = null, TimeProvider? time = null,
    GuildProjection? guild = null,
    DfoServer.Game.Dungeons.DungeonPassiveDropPolicy? passiveDrops = null, DfoServer.Game.Inventory.Services.DevilContractService? devil = null,
    DfoServer.Game.Creatures.CreatureService? creatures = null, CreatureProjection? creatureProjection = null,
    DfoServer.GameWorld.Quests.QuestStageGraph? stageGraph = null, DfoServer.GameWorld.Items.BoosterCatalog? boosters = null,
    NativeChannel? native = null, DfoServer.GameWorld.Items.ItemCatalog? items = null,
    ExperienceBonusService? experienceBonuses = null,
    DfoServer.Network.Projection.Inventory.SkinStorageProjection? skins = null, DfoServer.Game.ExpertJob.ExpertJobStoreBoard? expertStores = null,
    TownPresence? presence = null, CharacterLevelProjection? levels = null) : IConnectionProtocol
{
    public async Task RunAsync(ConnectionContext connection)
    {
        using var login = await loginHandler.AuthenticateAsync(connection);
        if (login is null)
            return;
        await using var sender = new GameConnectionSender(login.Owner, connection.Stream, connection.Lifetime, login.Sequence, (kind, opcode, sequence, body, action) =>
        {
            // 保活回包不逐条记 send；连接心跳由 KeepaliveHandler 的 keepalive 汇总覆盖。
            if (KeepaliveHandler.IsKeepalive(opcode))
                return;
            log.Write("send", new { Connection = connection.Id, Kind = kind, Opcode = opcode, Name = kind == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString(), Sequence = sequence, Action = action, Body = kind == 1 && PacketCapture.IsAuthentication(opcode) ? "[login-redacted]" : Convert.ToHexString(body.Span) });
        }, connection.CaptureSent);
        var session = new GameSession(repository, towns, login.Owner, login.Identity, quests, debugSettings, login.Channel.Entry,questEffects, contracts,
            timeProvider: time, passiveDrops: passiveDrops, creatures: creatures, stageGraph: stageGraph, items: items, experienceBonuses: experienceBonuses);
        var context = new GameConnectionContext(connection, login.Owner, session, sender, createActivity?.Invoke(session));
        if (creatures is not null && creatureProjection is not null)
            context.Creatures = new(context, creatures, creatureProjection, log);
        if (boosters is not null) context.BoosterUses = new(context, boosters, log, time);
        CubeContractConsumptionProjection? cubeConsumption = null;
        if (contracts is not null)
        {
            var cubeService = new DfoServer.Game.Inventory.Services.CubeContractService(contracts);
            context.Premiums = new(context, cubeService, stats ?? throw new InvalidOperationException("契约缺少属性投影"), time, devil, inventory, projection);
            cubeConsumption = new(context, cubeService, inventory ?? throw new InvalidOperationException("契约缺少库存投影"), log, time);
        }
        var elevator = new DungeonElevatorProjection(context, log);
        context.Elevator = elevator;
        var altar = new DungeonAltarProjection(context, log, projection, inventory, levels, time);
        context.Altar = altar;
        var death = new DungeonDeathProjection(context, projection, log, time);
        context.Death = death;
        var revival = new DungeonRevivalProjection(context, log, time);
        session.ReturnToSelectionRequested = async () =>
        {
            await context.CommandGate.WaitAsync(connection.Stop);
            try { await projection.ReturnToSelectionAsync(context); }
            finally { context.CommandGate.Release(); }
        };
        if (mailbox is not null)
            session.MailboxArrived = receipt => mailbox.Alarm(context, receipt);
        if (guild is not null)
        {
            // 建会认证的两个跨连接入口都绑定本条连接：邀请/取消推给被邀请方，建会结果（金币差额 + 3 个通知）发布给创建者。
            session.GuildPermitNoticeArrived = notice => guild.Notice(context, notice);
            session.GuildPermitResolved = outcome => guild.ResolvedAsync(context, outcome);
        }
        // 聊天投递入口绑定本条连接：服务端从在线会话目录按快照选中本会话后，由这里把 0x0C/0x76 入本连接队列。
        session.ChatNoticeArrived = notice => ChatProjection.Notice(context, notice);
        session.ExpertJobStoreClosed = id =>
        {
            if (expertStores?.TryClose(id, out _) == true)
                presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.CLOSE_EXPERT_JOB_STORE,
                    ExpertJobPackets.CloseStoreNotice(id), "expert_job_store_invalidated");
        };
        NativeChannel.Registration? nativeRegistration = null;
        try
        {
            online.Register(login.Owner, session);
            // GM 聊天帧注入按连接号定位：回调只把候选 opcode/正文入本连接队列，就绪与角色守卫在发送泵内复核。
            online.RegisterConnection(connection.Id, session, (opcode, body, action) =>
                sender.Post(0, opcode, body, action,
                    () => session.Ready && session.Selected is not null),
                new OnlineExpertJobConnection(
                    () => !connection.Stop.IsCancellationRequested && context.CommandGate.Wait(0),
                    () => context.CommandGate.Release(),
                    () => connection.Stop.IsCancellationRequested || context.Activity?.BlocksTownActions == true,
                    context.Disconnect,
                    async (result, info) =>
                    {
                        if (result.Receipt is not { } receipt || inventory is null) return;
                        bool Guard() => session.CanPublishInventory(receipt);
                        await inventory.RefreshAsync(context, result);
                        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(receipt.Character, context.Channel,
                            adventureName: session.ReadAdventureName(), premiumPcRoom: session.PremiumPcRoom),
                            "expert_job_customer_owner_actor", Guard);
                        await context.Notify(NotiPacketType.EXPERT_JOB_INFO, info, "expert_job_customer_owner_info", Guard);
                        projection.Refresh(context);
                    }));
            nativeRegistration = native?.Register(context);
            adventure?.Prepare(context);
            await context.Reply(CmdPacketType.LOGIN, ChannelPackets.Endpoint(connection.ServerAddress(options),
                native?.Port ?? login.Channel.Port, nativeRegistration?.Token ?? 0), "endpoint");
            var roster = session.PrepareRoster();
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity), "roster",
                () => session.CanPublishRoster(roster));
            session.MarkRosterPublished(roster);
            // 忙锁重试耗尽只记录诊断并丢弃当前包；记录阶段、opcode 与尝试次数，便于和同连接其它包关联。
            void LogBusy(string stage, Packet request, string name, int attempts, SqliteException error) =>
                log.Write("db_busy", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind, request.Opcode,
                    Name = name, Stage = stage, Attempts = attempts, Message = error.Message });

            while (!connection.Stop.IsCancellationRequested)
            {
                var request = GameFrame.ParseClient(await connection.ReadAsync());
                bool loginPacket = request.Opcode is (ushort)CmdPacketType.LOGIN or (ushort)CmdPacketType.LOGIN_PRECHECK;
                if (loginPacket)
                    throw new InvalidDataException("Login already completed on this connection");
                string name = ((CmdPacketType)request.Opcode).ToString();
                string body = PacketCapture.IsAuthentication(request.Opcode) ? "[login-redacted]" : Convert.ToHexString(request.Body);
                // 收包只记录一次：忙锁重试重放的是本包分派与刷新，不重复收包日志。
                // 保活包每 10 秒一次，逐包 recv/send 会刷屏：由 KeepaliveHandler 每连接每 60 秒汇总一条 keepalive。
                if (!KeepaliveHandler.IsKeepalive(request.Opcode))
                    log.Write("recv", new { Connection = connection.Id, request.Kind, request.Opcode, Name = name, Body = body });
                // 任务日周期与命令分派作为同一个重试单元：忙锁表示本次写事务未提交，重放不会把已提交结果记成成功；
                // 成功应答在提交之后才发送，重试期间客户端不会先收到成功。
                var (dispatched, result) = await SqliteBusyRetry.TryExecuteAsync(async () =>
                {
                    // 另一连接的副职业交易会保留本门禁到发布完成；日周期也不能绕过它替换 Selected。
                    await context.CommandGate.WaitAsync(connection.Stop);
                    try
                    {
                    if (session.AdvanceQuestCycle(DateTimeOffset.UtcNow) is { } cycle)
                    {
                        bool Guard() => session.CanPublishQuestCycle(cycle);
                        if (!cycle.Before.ActiveMainQuests.SequenceEqual(cycle.Character.ActiveMainQuests))
                            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(cycle.Character.ActiveMainQuests), "quest_auto_daily_cycle", Guard);
                        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(cycle.Character), "quest_daily_cycle", Guard);
                        if (cycle.Phase == SessionPhase.Town && quests is not null)
                            await new QuestProjection(quests).RefreshAsync(context, cycle.Character, Guard);
                    }
                    }
                    finally { context.CommandGate.Release(); }
                    return await commands.DispatchAsync(context, request);
                }, connection.Stop, (error, attempts) => LogBusy("dispatch", request, name, attempts, error));
                // 瞬时忙锁不是连接故障：丢掉这一包继续读下一帧，会话保持在线；其它数据库错误仍按原语义关闭连接。
                if (!dispatched)
                    continue;
                if (result == CommandResult.CloseConnection)
                    return;
                // 提交后的自动刷新单独重试，不与已完成的命令一起重放。
                var (refreshed, _) = await SqliteBusyRetry.TryExecuteAsync(async () =>
                {
                    await context.CommandGate.WaitAsync(connection.Stop);
                    try
                    {
                        await revival.RefreshAsync();
                        if (context.BoosterUses is { } boosterUses) await boosterUses.RefreshAsync();
                        if (context.Premiums is { } premiums) await premiums.RefreshAsync();
                        if (cubeConsumption is not null) await cubeConsumption.RefreshAsync();
                        if (context.Creatures is { } creatureClock) await creatureClock.RefreshAsync();
                        if (skins is not null) await skins.RefreshAsync(context);
                    }
                    finally { context.CommandGate.Release(); }
                    return true;
                }, connection.Stop, (error, attempts) => LogBusy("refresh", request, name, attempts, error));
                if (!refreshed)
                    continue;
                if (result == CommandResult.Unhandled)
                    log.Write("unimplemented", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind,
                        request.Opcode, Name = name, Length = request.Body.Length, Body = body });
            }
        }
        finally
        {
            nativeRegistration?.Dispose();
            await revival.DisposeAsync();
            if (context.BoosterUses is { } boosterUses) await boosterUses.DisposeAsync();
            if (context.Creatures is { } creatureClock) await creatureClock.DisposeAsync();
            if (cubeConsumption is not null) await cubeConsumption.DisposeAsync();
            await death.DisposeAsync();
            if (context.Cards is not null) await context.Cards.DisposeAsync();
            await elevator.DisposeAsync();
            await altar.DisposeAsync();
            online.RemoveConnection(connection.Id, session);
            online.Remove(login.Owner.Account, session);
            projection.Remove(context);
            context.Activity?.Reset();
            session.End();
            // 随后的 await using 先停止发送泵，再由 login 释放精确 owner。
        }
    }
}
