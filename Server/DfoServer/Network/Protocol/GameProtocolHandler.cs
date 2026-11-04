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
    DfoServer.GameWorld.Items.GoldLimitCatalog? goldLimits = null, DfoServer.GameWorld.Items.EpicPieceCatalog? pieces = null,
    ExperienceBonusService? experienceBonuses = null,
    DfoServer.Network.Projection.Inventory.SkinStorageProjection? skins = null, DfoServer.Game.ExpertJob.ExpertJobStoreBoard? expertStores = null,
    TownPresence? presence = null,
    DfoServer.GameWorld.Dungeons.DungeonIncountCatalog? incounts = null, CharacterLevelProjection? levels = null,
    DfoServer.Game.Social.SocialService? social = null, DfoServer.Game.Party.PartyService? parties = null,
    DfoServer.GameWorld.Dungeons.OperationHopeCatalog? operationHope = null, Action<GameConnectionContext>? prepareDungeon = null,
    GuildManagementProjection? guildManagement = null, DfoServer.Game.Guild.GuildInvitations? guildInvitations = null,
    GuildCargoProjection? guildCargo = null, GuildEconomyProjection? guildEconomy = null,
    GuildTeleportHandler? guildTeleport = null, DfoServer.Game.Guild.GuildTeleportService? guildTeleportService = null,
    GuildNpcProjection? guildNpcs = null, bool enableLukeRaidStart = false,
    DfoServer.Game.Pvp.PvpArenaService? arena = null,
    DfoServer.GameWorld.Guilds.GuildDungeonCatalog? guildDungeons = null,
    DfoServer.Game.Pvp.PvpRatingSettlement? pvpRatings = null,
    TagEvildomRunProjection? tagEvildomRun = null,
    ImperialArenaProjection? imperialArena = null,
    DfoServer.Game.Dungeons.DungeonBattleLevelSettings? monsterBattleLevels = null,
    DfoServer.Game.Dungeons.DungeonMonsterAiSettings? monsterAi = null,
    DfoServer.Game.Inventory.Services.ServerItemCycleService? itemCycles = null,
    DfoServer.Game.Raids.LukeRaidRejoin? lukeRejoin = null,
    DfoServer.Game.Guild.GuildNpcBenefitService? guildNpcBenefits = null,
    TradeHandler? trade = null,
    PvpMissionProjection? pvpMissions = null, DfoServer.Game.Pvp.PvpMissionBook? missionBook = null,
    DfoServer.Game.Events.ServerEventService? serverEvents = null,
    DfoServer.Game.CashShopPromotions.ICashShopPromotionStore? promotionStore = null, SettingsProjection? settingsProjection = null,
    DfoServer.GameWorld.Items.RecoveryConsumableCatalog? consumables = null,
    DfoServer.Game.Dungeons.SpringTreasureDropPolicy? springTreasure = null,
    DfoServer.GameWorld.Dungeons.Strongway2019Catalog? strongway2019 = null,
    Func<DfoServer.Game.Characters.Character, int>? strongway2019AntiEvil = null,
    DfoServer.Game.Strongway2019.Strongway2019MatchingService? strongwayMatching = null,
    Strongway2019Projection? strongwayProjection = null,
    DfoServer.GameWorld.PcRoom.PcRoomCatalog? pcRoom = null,
    DfoServer.Game.Events.PcRoomTimePointService? pcRoomTimePoint = null,
    DfoServer.Game.Dungeons.RiftNamedEpicDropPolicy? riftNamedEpics = null,
    DfoServer.Game.Events.EndlessAdventurePolicy? endlessAdventure = null,
    DfoServer.Game.Events.DailyAttendanceService? dailyAttendance = null,
    DfoServer.Game.Events.WeeklyAttendanceService? weeklyAttendance = null) : IConnectionProtocol
{
    DfoServer.Game.Dungeons.HostedDungeonEligibilityCoordinator? hostedDungeonEligibility;

    internal void AttachHostedDungeonEligibility(DfoServer.Game.Dungeons.HostedDungeonEligibilityCoordinator? coordinator) =>
        hostedDungeonEligibility = coordinator;

    // 断线诊断不得阻止剩余原清理，包括诊断后端自身失败；不以日志成功作为登记成功。
    void LogLukeDisconnect(string kind, object detail)
    {
        try { log.Write(kind, detail); }
        catch (Exception) { }
    }

    // 已提交战况的单成员投递边界；数据库读失败只结束本连接，不中断其余成员。
    internal static Action<DfoServer.Game.Raids.AntonRaidBattleView, DfoServer.Game.Raids.RaidActor> CreateRaidBattleNotice(
        GameConnectionContext context, ServerLog log,
        Action<DfoServer.Game.Raids.AntonRaidBattleView, DfoServer.Game.Raids.RaidActor> publish) => (view, actor) =>
    {
        try { publish(view, actor); }
        catch (System.Data.Common.DbException error)
        {
            try
            {
                log.Write("game_send_failed", new
                {
                    Connection = context.Id, Stage = "raid_battle_notice", Character = actor.Character,
                    view.Instance, view.Phase, view.Revision, Reason = error.GetType().Name,
                    Code = error.ErrorCode, SqliteCode = (error as SqliteException)?.SqliteErrorCode,
                    ExtendedCode = (error as SqliteException)?.SqliteExtendedErrorCode
                });
            }
            finally
            {
                try { context.Disconnect(); }
                catch (AggregateException callbacks)
                {
                    // Cancel已请求停止；回调失败单独记录，继续其它成员通知。
                    log.Write("game_send_failed", new { Connection = context.Id, Stage = "raid_battle_disconnect",
                        Reason = callbacks.GetType().Name, Callbacks = callbacks.InnerExceptions.Count });
                }
            }
        }
    };

    // 同一服务端进程内所有频道共享：记录账号上一次选角的频道类型，供原生换频道判断客户端快照。
    readonly ChannelHistory channelHistory = new();

    public async Task RunAsync(ConnectionContext connection)
    {
        DfoServer.Game.Raids.AntonRaidDisconnectFact? lukeDisconnect = null;
        // 外层finally在sender和login/owner释放之后执行；不改变原会话清理顺序。
        try
        {
        using var login = await loginHandler.AuthenticateAsync(connection);
        if (login is null)
            return;
        // 资源映射在账号状态锁外共享预热，死亡冻结阶段只查询不可变结果。
        _ = consumables?.EpicDropBuffs;
        _ = consumables?.HellLegendEpicDropBuffs;
        _ = consumables?.VillageMoveSpeedBuffs;
        _ = consumables?.EnhancementPotions;
        await using var sender = new GameConnectionSender(login.Owner, connection.Stream, connection.Lifetime, login.Sequence, (kind, opcode, sequence, body, action) =>
        {
            // 保活回包不逐条记 send；连接心跳由 KeepaliveHandler 的 keepalive 汇总覆盖。
            if (KeepaliveHandler.IsKeepalive(opcode))
                return;
            log.Write("send", new { Connection = connection.Id, Kind = kind, Opcode = opcode, Name = kind == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString(), Sequence = sequence, Action = action, Body = kind == 1 && PacketCapture.IsSensitiveCommand(opcode) ? PacketCapture.RedactedCommandBody(opcode) : Convert.ToHexString(body.Span) });
        }, connection.CaptureSent, (stage, error) => log.Write("game_send_failed", new
        {
            Connection = connection.Id, Stage = stage, Reason = error?.GetType().Name, Message = error?.Message
        }));
        var session = new GameSession(repository, towns, login.Owner, login.Identity, quests, debugSettings, login.Channel.Entry,questEffects, contracts,
            timeProvider: time, passiveDrops: passiveDrops, creatures: creatures, stageGraph: stageGraph, items: items,
            pieces: pieces, experienceBonuses: experienceBonuses, incounts: incounts,
            channelHistory: channelHistory, operationHope: operationHope, guildDungeons: guildDungeons, pvpRatings: pvpRatings,
            monsterBattleLevels: monsterBattleLevels, monsterAi: monsterAi, itemCycles: itemCycles, guildNpcBenefits: guildNpcBenefits, promotionStore: promotionStore, consumables: consumables, springTreasure: springTreasure,
            strongway2019Catalog: strongway2019, strongway2019AntiEvil: strongway2019AntiEvil, serverEvents: serverEvents,
            directPickupGoldLimits: goldLimits, pcRoomTimePoint: pcRoomTimePoint, riftNamedEpics: riftNamedEpics,
            endlessAdventure: endlessAdventure, dailyAttendance: dailyAttendance);
        session.AttachHostedDungeonEligibility(hostedDungeonEligibility);
        var context = new GameConnectionContext(connection, login.Owner, session, sender, createActivity?.Invoke(session))
            { RaidClearLukeCycle = projection.RaidClearLukeCycle };
        context.ChatEmoticons = settingsProjection?.CreateChatEmoticons(context);
        if (pcRoom is not null)
        {
            session.ConfigurePcRoom(pcRoom);
            context.PcRoom = new(context, time);
        }
        if (creatures is not null && creatureProjection is not null)
            context.Creatures = new(context, creatures, creatureProjection, log);
        if (boosters is not null) context.BoosterUses = new(context, boosters, log, time,
            imperialArena is null ? null : () => imperialArena.RefreshPeriodAsync(context));
        context.Dazzlement = new(context, log, time);
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
        var lostLand = new LostLandProjection(context, projection, log, time);
        context.LostLand = lostLand;
        var death = new DungeonDeathProjection(context, projection, log, time);
        context.Death = death;
        var revival = new DungeonRevivalProjection(context, log, time);
        var disguise = new ConsumableDisguiseProjection(context, projection, log, time);
        var tagRevival = new TagEvildomRevivalProjection(context, log, time);
        var tagDeadline = tagEvildomRun is null ? null : new TagEvildomDeadlineProjection(context, projection, tagEvildomRun, log, SqliteBusyRetry.IsBusy, time);
        var dailyFatigue = new ServiceDayProjection(context, log, projection, time, inventory, missionBook, pvpMissions);
        if (dailyAttendance is not null) context.DailyAttendance = new(context, dailyAttendance, time ?? TimeProvider.System, log);
        if (pcRoomTimePoint is not null) context.PcRoomTimePoint = new(context, pcRoomTimePoint, projection, log, time, SqliteBusyRetry.IsBusy);
        if (weeklyAttendance is not null) context.WeeklyAttendance = new(context, weeklyAttendance, projection, log);
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
        session.GuildManagementNoticeArrived = notice =>
        {
            guildManagement?.Post(context, notice);
            guildEconomy?.Post(context, force: notice.Kind == DfoServer.Game.Guild.GuildManagementNoticeKind.Refresh);
            if (notice.NpcsChanged) guildNpcs?.Post(context);
        };
        session.GuildCargoNoticeArrived = notice => guildCargo?.Post(context, notice);
        session.GuildManagementClosing = () => { guildInvitations?.Close(session); guildEconomy?.Close(session); guildTeleportService?.Close(session); };
        guildTeleport?.PrepareContext(context);
        session.GuildPresenceFailed = failure => log.Write("guild_presence_failed", new
        {
            Connection = connection.Id, Character = failure.CharacterId,
            ObservedAt = failure.ObservedAtUnix, Error = failure.Error.Message
        });
        // 聊天投递入口绑定本条连接：服务端从在线会话目录按快照选中本会话后，由这里把 0x0C/0x76 入本连接队列。
        session.ChatNoticeArrived = notice => ChatProjection.Notice(context, notice);
        session.EmoticonArrived = notice => context.Sender.Post(0, (ushort)NotiPacketType.EMOTICON,
            DfoServer.Network.Builders.Inventory.SkinStoragePackets.Emoticon(notice.MemberId, notice.SkinId),
            "instant_emoticon", notice.Guard);
        session.ExpertJobStoreClosed = id =>
        {
            if (expertStores?.TryClose(id, out _) == true)
                presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.CLOSE_EXPERT_JOB_STORE,
                    ExpertJobPackets.CloseStoreNotice(id), "expert_job_store_invalidated");
        };
        session.SocialNoticeArrived = view => SocialProjection.Notice(context, view);
        session.PvpArenaNoticeArrived = notice => PvpArenaProjection.Notice(context, notice, stats);
        session.PvpArenaResultArrived = delivery =>
        {
            if (arena?.CanPublishResultDelivery(delivery) == true && missionBook is not null)
                if (pvpMissions?.QueueRewards(context, session.RefreshPvpMissions(missionBook)) == false) return false;
            bool queued = arena is not null && PvpArenaResultProjection.TryQueue(context, arena, delivery, pvpMissions);
            log.Write("pvp_arena_result_queued", new { Connection = context.Id, Match = delivery.Match.Id,
                Character = delivery.Receipt.CharacterId, Queued = queued, Rule = delivery.Receipt.RuleVersion });
            return queued;
        };
        session.SocialAccountDeleted = (name, guard) => sender.Post(0, (ushort)NotiPacketType.ACCOUNT_FRIEND_DELETE,
            SocialPackets.Name(name), "account_friend_deleted", guard);
        ushort socialCharacter = 0;
        session.PartyNoticeArrived = notice => PartyProjection.Notice(context, notice);
        session.RaidNoticeArrived = (change, actor) => RaidProjection.Notice(context, change, actor, enableLukeRaidStart, projection);
        session.RaidBattleNoticeArrived = CreateRaidBattleNotice(context, log,
            (view, actor) => RaidCombatProjection.Notice(context, view, actor));
        session.RaidRunClearedArrived = (cleared, actor) => RaidCombatProjection.RunCleared(context, cleared, actor);
        session.AutomaticRaidRewardArrived = async receipt =>
        {
            if (inventory is not null) await inventory.RefreshAsync(context, receipt.Inventory);
            if (!receipt.Commit.MailIds.IsEmpty) session.NotifyMailbox(receipt.Prepared.Character);
            if (DfoServer.Game.BattlePass.BattlePassUndefeatedSongSource.DeclaresSong(receipt.Prepared) && session.CanPublishRaidReward(receipt))
                await BattlePassMissionProjection.RefreshAsync(context, (time ?? TimeProvider.System).GetUtcNow());
            if (items is not null && DfoServer.Game.Raids.LukeRewardResults.Bind(receipt, items,
                (time ?? TimeProvider.System).GetUtcNow()) is { } result)
            {
                _ = context.LukeResults.Post(context, receipt, result);
                await context.Sender.DrainAsync();
            }
        };
        session.AutomaticLukeRecoveryArrived = async result =>
        {
            if (inventory is not null) await inventory.RefreshAsync(context, result);
            // 任务为账号共享；原回调已安装当前选角快照，不把旧角色资产发给其它角色。
            if (result.Receipt is { } receipt && session.CanPublishInventory(receipt))
                await BattlePassMissionProjection.RefreshAsync(context, (time ?? TimeProvider.System).GetUtcNow());
        };
        session.AntonAutoRewardRequested = (battle, phase) =>
        {
            if (repository is not DfoServer.Game.Raids.IRaidRewardStore rewardStore || items is null ||
                goldLimits is null || pieces is null || inventory is null) return false;
            // 本人资产提交及FIFO入队与同连接请求互斥；不等待共享门禁，不升级成跨成员账号锁。
            if (!context.CommandGate.Wait(0)) return false;
            try
            {
                return session.AutoClaimAntonReward(battle, phase, rewardStore, items, goldLimits, pieces,
                    (time ?? TimeProvider.System).GetUtcNow());
            }
            finally { context.CommandGate.Release(); }
        };
        session.AntonAutoRewardArrived = receipt =>
        {
            if (inventory is null) return;
            using var publication = sender.QueuePublication(() => session.CanPublishRaidReward(receipt));
            // QueuePublication使Refresh只排入原有有界队列，此处没有网络等待或后台Task。
            try { inventory.RefreshAsync(context, receipt.Inventory).GetAwaiter().GetResult(); }
            catch (IOException) { context.Disconnect(); return; }
            if (!receipt.Commit.MailIds.IsEmpty) session.NotifyMailbox(receipt.Prepared.Character);
            // 普通团非空600第一尾尚未闭合；保留真实资产与回执，不发8B错误结果。
            if (AntonRewardPackets.TryFrozenResult(receipt.Prepared.Kind, receipt.Prepared.Character, receipt.Prepared.Card,
                receipt.Prepared.Grants, out var body, out var missing))
                sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST, body, "raid_reward_display",
                    () => session.CanPublishRaidReward(receipt) && session.RaidCombatView() is
                        { State: DfoServer.Game.Raids.AntonRaidBattleState.Reward } view && view.Phase == receipt.Prepared.Phase);
            else
                log.Write("raid_reward_display_unimplemented", new { Connection = context.Id, receipt.Prepared.Phase,
                    receipt.Prepared.Kind, Reason = "anton-general600-tail1-unverified", MissingFields = missing });
            log.Write("raid_reward_committed", new { Connection = context.Id, receipt.Prepared.Instance,
                receipt.Prepared.Phase, receipt.Prepared.Kind, receipt.Prepared.Card, receipt.Commit.Duplicate, Automatic = true });
        };
        session.RaidStartArrived = (receipt, actor) => RaidProjection.Started(context, projection, receipt, actor);
        session.RaidCostArrived = (rows, actor) => RaidProjection.Cost(context, rows, actor);
        session.RaidRecordNoticeArrived = (party, actor) => sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY,
            RaidProjection.Record(party, enableLukeRaidStart), "raid_record_updated", () => session.CanPublishRaid(actor, party));
        session.RaidPeerNoticeArrived = (recipient, peer, peerId, invite) => RaidProjection.Peer(context, recipient, peer, peerId, invite);
        session.RaidDirectoryNoticeArrived = (change, actor) => RaidProjection.Directory(context, change, actor, enableLukeRaidStart);
        session.RaidPeerRefused = actor => sender.Post(1, (ushort)CmdPacketType.RESPONSE_PEER,
            PartyPackets.Failure(133, 10), "raid_peer_refused", () => actor.IsCurrent);
        session.HasMultiplePartyMembers = () => parties?.HasMultipleMembers(session) == true;
        session.CurrentParty = () => parties?.Current(session);
        session.EvildomWarCleanupFailed = error => log.Write("evildom_war_cleanup_failed", new
            { Connection = connection.Id, Error = error.GetType().Name, error.Message });
        prepareDungeon?.Invoke(context);
        if (strongwayProjection is not null)
        {
            context.Strongway2019 = strongwayProjection;
            session.Strongway2019NoticeArrived = notice => strongwayProjection.DeliverAsync(context, notice);
            session.Strongway2019RecoveryArrived = character => strongwayProjection.RecoverReadyRewardsAsync(context, character);
        }
        long partyEpoch = session.SelectionEpoch;
        DfoServer.Game.Trade.TradeParticipant? tradeParticipant = null;
        await using var partyDirectory = parties is null ? null : new PartyDirectoryProjection(context, parties, towns, log);
        context.PartyDirectory = partyDirectory;
        await using var eventProjection = serverEvents is null ? null : new ServerEventProjection(context, serverEvents, projection, log, time ?? TimeProvider.System);
        context.ServerEvents = eventProjection;
        await using var arenaDirectory = arena is null ? null : new PvpArenaDirectoryProjection(context, arena, log);
        long directoryEpoch = sender.Epoch;
        var directoryPhase = session.Phase;
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
                        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(receipt.Character, context.Session.ReadPvpGrade(receipt.Character), context.Channel,
                            adventureName: session.ReadAdventureName(), premiumPcRoom: session.PremiumPcRoom),
                            "expert_job_customer_owner_actor", Guard);
                        await context.Notify(NotiPacketType.EXPERT_JOB_INFO, info, "expert_job_customer_owner_info", Guard);
                        projection.Refresh(context);
                    }),
                new OnlineMarriageConnection(
                    result => inventory?.RefreshAsync(context, result) ?? Task.CompletedTask,
                    (opcode, body, action, guard) => context.Notify((NotiPacketType)opcode, body, action, guard),
                    (opcode, body, action, guard) => context.Reply((CmdPacketType)opcode, body, action, guard),
                    receipt => projection.TravelAsync(context, receipt),
                    () => { if (session.Phase == SessionPhase.Town) projection.Refresh(context, marriageOnly: true); }),
                guardedProbe: (opcode, body, action, guard) => sender.TryPost(0, opcode, body, action,
                    () => !connection.Stop.IsCancellationRequested && session.Ready && session.Selected is not null && guard()),
                refreshFatigue: () => FatigueProjection.PostCurrent(context));
            nativeRegistration = native?.Register(context);
            session.SetPeerAccount(nativeRegistration?.Token ?? 0);
            adventure?.Prepare(context);
            await context.Reply(CmdPacketType.LOGIN, ChannelPackets.Endpoint(connection.ServerAddress(options),
                native?.Port ?? login.Channel.Port, nativeRegistration?.Token ?? 0, ChannelPackets.LoginChannelType(login.Channel.Entry)), "endpoint");
            var roster = session.PrepareRoster();
            await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity, items), "roster",
                () => session.CanPublishRoster(roster));
            session.MarkRosterPublished(roster);
            // 忙锁重试耗尽只记录诊断并丢弃当前包；记录阶段、opcode 与尝试次数，便于和同连接其它包关联。
            void LogBusy(string stage, Packet request, string name, int attempts, SqliteException error) =>
                log.Write("db_busy", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind, request.Opcode,
                    Name = name, Stage = stage, Attempts = attempts, Message = error.Message });

            var clientSequence = new ClientRequestSequence();
            while (!connection.Stop.IsCancellationRequested)
            {
                var request = GameFrame.ParseClient(await connection.ReadAsync());
                request = request with { RequestSerial = clientSequence.Observe(request.Sequence) };
                session.ObserveGuildRequest(request.RequestSerial);
                bool loginPacket = request.Opcode is (ushort)CmdPacketType.LOGIN or (ushort)CmdPacketType.LOGIN_PRECHECK;
                if (loginPacket)
                    throw new InvalidDataException("Login already completed on this connection");
                string name = ((CmdPacketType)request.Opcode).ToString();
                string body = PacketCapture.ReceivedCommandBody(request.Opcode, request.Body);
                // 收包只记录一次：忙锁重试重放的是本包分派与刷新，不重复收包日志。
                // 保活包每 10 秒一次，逐包 recv/send 会刷屏：由 KeepaliveHandler 每连接每 60 秒汇总一条 keepalive。
                if (!KeepaliveHandler.IsKeepalive(request.Opcode))
                    log.Write("recv", new { Connection = connection.Id, request.Kind, request.Opcode, Name = name, Body = body });
                // 任务日周期与命令分派作为同一个重试单元：忙锁表示本次写事务未提交，重放不会把已提交结果记成成功；
                // 成功应答在提交之后才发送，重试期间客户端不会先收到成功。
                var beforeTrade = session.CapturePlayerTrade(requireEligible: false);
                tradeParticipant = beforeTrade?.Participant ?? tradeParticipant;
                var beforeTradeView = tradeParticipant is { } capturedTrade ? trade?.Find(capturedTrade) : null;
                var (dispatched, result) = await SqliteBusyRetry.TryExecuteAsync(async () =>
                {
                    // 另一连接的副职业交易会保留本门禁到发布完成；日周期也不能绕过它替换 Selected。
                    await context.CommandGate.WaitAsync(connection.Stop);
                    try
                    {
                    if (tagDeadline is not null) await tagDeadline.CheckAsync();
                    if (session.AdvanceQuestCycle(DateTimeOffset.UtcNow) is { } cycle)
                    {
                        bool Guard() => session.CanPublishQuestCycle(cycle);
                        if (!cycle.Before.ActiveMainQuests.SequenceEqual(cycle.Character.ActiveMainQuests))
                            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(cycle.Character.ActiveMainQuests), "quest_auto_daily_cycle", Guard);
                        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(cycle.Character), "quest_daily_cycle", Guard);
                        if (cycle.Phase == SessionPhase.Town && quests is not null)
                            await new QuestProjection(quests).RefreshAsync(context, cycle.Character, Guard);
                    }
                    await dailyFatigue.PublishAsync();
                    }
                    finally { context.CommandGate.Release(); }
                    return await commands.DispatchAsync(context, request);
                }, connection.Stop, (error, attempts) => LogBusy("dispatch", request, name, attempts, error));
                // 瞬时忙锁不是连接故障：丢掉这一包继续读下一帧，会话保持在线；其它数据库错误仍按原语义关闭连接。
                if (!dispatched)
                    continue;
                if (result == CommandResult.CloseConnection)
                    return;
                var afterTrade = session.CapturePlayerTrade(requireEligible: false);
                if (beforeTrade is { } before && afterTrade is { } after && before.Participant == after.Participant &&
                    (before.Character.Position.Town != after.Character.Position.Town || before.Character.Position.Area != after.Character.Position.Area))
                {
                    if (beforeTradeView is { } previousView) trade?.Release(before.Participant, previousView);
                    tradeParticipant = null;
                }
                if (!session.Ready || session.SelectionEpoch != partyEpoch)
                {
                    if (tradeParticipant is { } previous && beforeTradeView is { } previousView) trade?.Release(previous, previousView);
                    tradeParticipant = null;
                    if (strongwayMatching?.MatchFor(session) is { } previousStrongway && strongwayProjection is not null)
                        await strongwayProjection.TerminalAsync(previousStrongway);
                    strongwayMatching?.Detach(session, session.Ready ? session.SelectionEpoch : null);
                    await PartyDungeonProjection.DetachAsync(context);
                    // 只拆上一选角代次；Ready期间恢复的真实席位已经绑定当前代次。
                    parties?.Detach(session, session.Ready ? session.SelectionEpoch : null);
                    if (arena is not null) arena.Publish(await arena.DetachAsync(session));
                }
                else if (session.Phase != SessionPhase.Town && tradeParticipant is { } previousTrade)
                {
                    if (beforeTradeView is { } previousView) trade?.Release(previousTrade, previousView);
                    tradeParticipant = null;
                }
                partyEpoch = session.SelectionEpoch;
                tradeParticipant ??= afterTrade?.Participant;
                ushort currentSocial = session.Ready ? session.Selected?.Id ?? (ushort)0 : (ushort)0;
                bool directoryChanged = socialCharacter != currentSocial || directoryEpoch != sender.Epoch || directoryPhase != session.Phase;
                if (socialCharacter != currentSocial)
                {
                    if (socialCharacter != 0) social?.PresenceChanged(socialCharacter, login.Owner.Account);
                    socialCharacter = currentSocial;
                }
                if (directoryChanged)
                {
                    directoryEpoch = sender.Epoch;
                    directoryPhase = session.Phase;
                    // 选图会移除城镇实体，也必须让其他观察者移除招募行；不能只刷新本人列表。
                    parties?.PresenceChanged();
                    arenaDirectory?.Request();
                }
                // 提交后的自动刷新单独重试，不与已完成的命令一起重放。
                var (refreshed, _) = await SqliteBusyRetry.TryExecuteAsync(async () =>
                {
                    await context.CommandGate.WaitAsync(connection.Stop);
                    try
                    {
                        if (session.EnsureQuestEligibilityMail() is { } invitation)
                        {
                            log.Write("quest_mail_arrived",new {Connection=context.Id,Character=invitation.Character.Id,Timing="first-eligible"});
                            mailbox?.Alarm(context,invitation);
                        }
                        await dailyFatigue.RefreshAsync();
                        if (context.PcRoomTimePoint is { } onlineRewards) await onlineRewards.RefreshAsync();
                        if (context.WeeklyAttendance is { } weekly) await weekly.RefreshAsync(force: request.Opcode == (ushort)CmdPacketType.SET_PLAY_RESULT);
                        await revival.RefreshAsync();
                        await disguise.RefreshAsync();
                        await tagRevival.RefreshAsync();
                        if (tagDeadline is not null) await tagDeadline.RefreshAsync();
                        if (context.BoosterUses is { } boosterUses) await boosterUses.RefreshAsync();
                        await context.Dazzlement.RefreshAsync();
                        if (context.Premiums is { } premiums) await premiums.RefreshAsync();
                        // 490的未勾选状态也会检查会员资格，必须在1760初始化网吧128之后发送。
                        if (context.PcRoom is { } pcRoom) await pcRoom.RefreshAsync();
                        if (cubeConsumption is not null) await cubeConsumption.RefreshAsync();
                        if (context.Creatures is { } creatureClock) await creatureClock.RefreshAsync();
                        if (skins is not null) await skins.RefreshAsync(context);
                        if (context.ChatEmoticons is { } chatEmoticons) await chatEmoticons.RefreshAsync();
                    }
                    finally { context.CommandGate.Release(); }
                    return true;
                }, connection.Stop, (error, attempts) => LogBusy("refresh", request, name, attempts, error));
                if (!refreshed)
                    continue;
                if (result == CommandResult.Unhandled)
                    log.Write("unimplemented", new { Connection = connection.Id, Phase = session.Phase.ToString(), request.Kind,
                        request.Opcode, Name = name, Length = request.Body.Length, Body = body });
                // 1278的ACK只证明回包；此处才是本请求分派与自动刷新均完成、门禁已释放的尾界。
                // 不表示独立后台任务永久空闲，不新增客户端协议，也不记录账号或请求正文。
                if (request.Opcode == (ushort)CmdPacketType.CHECK_USER_CONNECTION)
                    log.Write("command_completed", new { Connection = connection.Id, Generation = login.Owner.Generation,
                        session.SelectionEpoch, Character = session.Selected?.Id, request.Sequence, request.RequestSerial, request.Opcode });
            }
        }
        finally
        {
            if (tradeParticipant is { } departing) trade?.Release(departing);
            nativeRegistration?.Dispose();
            if (context.PcRoomTimePoint is { } onlineRewards) await onlineRewards.DisposeAsync();
            await dailyFatigue.DisposeAsync();
            await revival.DisposeAsync();
            await disguise.DisposeAsync();
            await tagRevival.DisposeAsync();
            if (tagDeadline is not null) await tagDeadline.DisposeAsync();
            if (context.BoosterUses is { } boosterUses) await boosterUses.DisposeAsync();
            await context.Dazzlement.DisposeAsync();
            if (context.Creatures is { } creatureClock) await creatureClock.DisposeAsync();
            if (cubeConsumption is not null) await cubeConsumption.DisposeAsync();
            await death.DisposeAsync();
            if (context.Cards is not null) await context.Cards.DisposeAsync();
            await elevator.DisposeAsync();
            await altar.DisposeAsync();
            await lostLand.DisposeAsync();
            if (context.LukePhaseExit is { } lukeExit) await lukeExit.DisposeAsync();
            // 此处不持本人owner；在普通队伍/团名册/Session移除前冻结原身份。
            if (lukeRejoin is not null)
            {
                try
                {
                    lukeDisconnect = lukeRejoin.TryCaptureForDisconnect(session, out string reason);
                    if (reason == "owner-busy-or-stale")
                        LogLukeDisconnect("raid_rejoin_candidate", new { Connection = connection.Id, RaidType = "Luke",
                            Recorded = false, Reason = reason, Recovery = "wire-withheld" });
                }
                catch (Exception error)
                {
                    LogLukeDisconnect("raid_rejoin_candidate_failed", new { Connection = connection.Id, Stage = "capture", Reason = error.GetType().Name });
                }
            }
            online.RemoveConnection(connection.Id, session);
            if (strongwayProjection is not null) await strongwayProjection.BeforeDepartureAsync(context);
            strongwayMatching?.Detach(session);
            await PartyDungeonProjection.DetachAsync(context);
            parties?.Detach(session);
            if (arena is not null) arena.Publish(await arena.DetachAsync(session));
            online.Remove(login.Owner.Account, session);
            if (socialCharacter != 0) social?.PresenceChanged(socialCharacter, login.Owner.Account);
            projection.Remove(context);
            context.Activity?.Reset();
            try
            {
                await SqliteBusyRetry.ExecuteAsync(() =>
                {
                    session.FlushPcRoomTimePoint();
                    return Task.FromResult(true);
                }, CancellationToken.None);
            }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation") { }
            finally { session.End(); }
            // 随后的 await using 先停止发送泵，再由 login 释放精确 owner。
        }
        }
        finally
        {
            if (lukeDisconnect is not null && lukeRejoin is not null)
            {
                try
                {
                    bool recorded = lukeRejoin.TryRecordDisconnected(lukeDisconnect, out string reason);
                    LogLukeDisconnect("raid_rejoin_candidate", new { Connection = connection.Id, Self = lukeDisconnect.Character,
                        RaidType = "Luke", lukeDisconnect.Instance, lukeDisconnect.Slot, lukeDisconnect.Squad,
                        Recorded = recorded, Reason = reason, Recovery = "wire-withheld" });
                }
                catch (Exception error)
                {
                    LogLukeDisconnect("raid_rejoin_candidate_failed", new { Connection = connection.Id, Stage = "record", Reason = error.GetType().Name });
                }
            }
        }
    }
}
