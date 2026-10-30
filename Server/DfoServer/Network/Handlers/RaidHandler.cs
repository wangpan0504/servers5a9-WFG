using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 招募及开战复用正式会话身份；首阶段提交与进图、战斗奖励按各自领域边界处理。
// 魔界大战不是团本：建队走 SET_PARTY_INFO(kind=5)，开战走 C2S 1652（见 DungeonHandler.EvildomWar），
// 这里不为它补建类型 6 的团本记录。
internal sealed class RaidHandler(ServerLog log, RaidCatalog catalog, RaidRegistry registry, OnlineSessionDirectory? online = null,
    AntonRaidStart? start = null, LukeRaidStart? lukeStart = null, AntonRaidRejoin? rejoin = null,
    Game.Party.PartyService? parties = null) : IGameCommandModule
{
    public RaidHandler(ServerLog log) : this(log, RaidCatalog.Empty, new RaidRegistry()) { }

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CREATE_RAID, CreateAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.LEAVE_RAID, LeaveAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.RAID_ENTRY_COST_INFO, EntryCostAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.START_RAID, StartAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.SET_RAID_WAITING, WaitingAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.MODIFY_RAID_INFO, ModifyAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.RAID_MANAGER_WORK, ManagerAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.REQUEST_RAID_INFO, InformationAsync, nameof(RaidHandler));
        commands.Register(CmdPacketType.RAID_OTHER_CHANNEL_LIST, SearchAsync, nameof(RaidHandler));
        // CMD668 REJOIN_RAID 与同号 NOTI668 REAL_ESTATE_STATE 是两套枚举，这里只注册命令。
        commands.Register(CmdPacketType.REJOIN_RAID, RejoinAsync, nameof(RaidHandler));
    }

    RaidActor? Actor(GameConnectionContext context) => context.BlocksTownActions ? null : context.Session.CaptureRaidActor(registry);
    // 频道类型只决定攻坚队类型；魔界大战（0x36 → 6）在 raid.lst 没有定义，不能要求目录存在。
    bool MatchesChannel(RaidActor actor, byte type) => actor.Session.ChannelEntry is { } channel &&
        RaidTypes.FromChannelType(channel.Type) == type;
    // 1361 查询只认 raid.lst 里真有定义的团本；魔界大战的组队走 SET_PARTY_INFO(kind=5)，
    // 不在这里补建类型 6 的记录，因此也不需要频道例外。
    bool MatchesType(RaidActor actor, byte type) => MatchesChannel(actor, type) && catalog.Find(type) is not null;

    async Task<CommandResult> SearchAsync(GameConnectionContext context, Packet request)
    {
        RaidSearchRequest parsed;
        try { parsed = RaidRequests.Search(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor || !MatchesType(actor, parsed.RaidType)) return CommandResult.Unhandled;
        // 当前频道的正式招募目录；不以未知跨频道加入合同发布其它频道的可加入记录。
        var snapshot = registry.List(actor.Channel, parsed.RaidType);
        var matches = snapshot.Where(p => p.Title.Contains(parsed.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        uint offset = Math.Min(parsed.Offset, (uint)matches.Length);
        // 02171960..0217196D按页号乘33发送偏移，保持同一页宽，不创建额外会话或后台查询。
        var rows = matches.Skip((int)offset).Take(33).ToArray();
        var body = RaidPackets.SearchResults((uint)matches.Length, offset, rows.Select(p =>
            (p.Key, p.TitleBytes, p.Type, p.Leader, CharacterName.Encoding.GetBytes(p.LeaderName),
                p.Members.Single(m => m.Actor.Character == p.Leader).Squad, RaidProjection.CanStart(p, lukeStart is not null),
                checked((byte)p.Members.Length))).ToArray());
        log.Write("raid_search_request", new { Connection = context.Id, parsed.RaidType, parsed.Offset,
            Total = matches.Length, Returned = rows.Length });
        // 查询回复是请求时的只读快照；目录并发变化不能吞掉唯一756、令原生窗口继续等待。
        await context.Notify(NotiPacketType.RAID_OTHER_CHANNEL_LIST, body, "raid_search_results", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> RejoinAsync(GameConnectionContext context, Packet request)
    {
        uint key;
        try { key = RaidRequests.Rejoin(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (context.Session.CaptureRaidRejoinActor(registry) is not { } actor) return CommandResult.Unhandled;
        if (rejoin is not { Enabled: true })
        {
            // ACK668的0055D870在Luke 0x20/Anton 0x17直接返回；成功ACK不安装团本或队伍，不能冒充恢复。
            log.Write("raid_start_request", new { Connection = context.Id, Self = actor.Character, Key = key.ToString("X8"),
                Error = RaidPackets.CreateUnavailable, Reason = "formal-raid-rejoin-unavailable" });
            await context.Reply(CmdPacketType.REJOIN_RAID, RaidPackets.CreateFailure(), "raid_rejoin_rejected", () => actor.IsCurrent);
            return CommandResult.Handled;
        }
        var decision = Decide(actor, key);
        var recovery = decision.Matched && parties is not null ? registry.RestoreAnton(actor, key, rejoin, parties) : null;
        log.Write("raid_rejoin_rejected", new { Connection = context.Id, Self = actor.Character, Key = key.ToString("X8"),
            decision.Reason, decision.OldEpoch, decision.NewEpoch, decision.Slot, decision.Squad,
            decision.OldSessionReleased, OwnerReplace = recovery?.Restored == true ? "bound-new-owner" : decision.OwnerReplace,
            LoadingBarrier = recovery is { Restored: true, Run: not null } ? "original-run-awaiting-client" : decision.LoadingBarrier, decision.Unknown,
            StateRestored = recovery?.Restored ?? false, StateReason = recovery?.Reason,
            Instance = recovery?.Instance, Run = recovery?.Run, Error = RaidPackets.CreateUnavailable });
        // 副本协议668仅证明key:u32与空成功体；状态提交可完成，未知恢复包序仍不发猜测成功。
        await context.Reply(CmdPacketType.REJOIN_RAID, RaidPackets.CreateFailure(), "raid_rejoin_rejected", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    // 入城完成后下发。客户端无本地团记录且 key 高位匹配时才会自动发 668；异频道不下发，避免走到缺低 16 位的提示回调。
    internal async Task NotifyDisconnAsync(GameConnectionContext context)
    {
        if (rejoin is not { Enabled: true } || context.Session.Selected is not { } character || !rejoin.HasCandidate(character.Id)) return;
        if (Actor(context) is not { } actor)
        {
            log.Write("raid_rejoin_rejected", new { Connection = context.Id, Character = character.Id, Reason = "rejoin-actor-unavailable" });
            return;
        }
        var notice = Describe(actor);
        if (!notice.Matched)
        {
            if (notice.Reason != "none")
                log.Write("raid_rejoin_rejected", new { Connection = context.Id, Self = actor.Character, Key = notice.Key?.ToString("X8"), notice.Reason });
            return;
        }
        uint key = notice.Key!.Value;
        log.Write("raid_rejoin_notice", new { Connection = context.Id, Self = actor.Character, Key = key.ToString("X8"),
            notice.OldEpoch, notice.NewEpoch, UnknownTail = "u8+u16", Tail = "00-0000", Reason = "835-tail-semantics-unknown" });
        await context.Notify(NotiPacketType.RAID_DISCONN_INFO, RaidPackets.DisconnInfo(key), "raid_rejoin_notice", () => actor.IsCurrent);
    }

    AntonRaidRejoinDecision Describe(RaidActor actor)
    {
        var fact = rejoin!.Peek(actor.Character);
        if (fact is null || fact.Account != actor.Owner.Account) return AntonRaidRejoinDecision.Reject("none");
        return rejoin.DescribeNotice(actor, registry.AntonBattleAlive(fact), registry.AntonMemberCurrent(fact));
    }

    AntonRaidRejoinDecision Decide(RaidActor actor, uint key)
    {
        var fact = rejoin!.Peek(actor.Character);
        bool alive = fact is not null && registry.AntonBattleAlive(fact);
        bool member = fact is not null && registry.AntonMemberCurrent(fact);
        return rejoin.Evaluate(actor, key, alive, member);
    }

    async Task<CommandResult> StartAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0 || Actor(context) is not { } actor) return CommandResult.Unhandled;
        var party = registry.Find(actor);
        var definition = party is null ? null : catalog.Find(party.Type);
        if (party?.Leader == actor.Character && context.Session.CurrentRaidBattle(actor) is { Kind: RaidKind.Anton } battle &&
            battle.View.State == AntonRaidBattleState.Intermission)
        {
            if (!battle.PrepareAntonNextPhase(actor)) return CommandResult.Unhandled;
            log.Write("anton_phase2_preparing", new { Connection = context.Id, battle.Instance, Reason = "leader" });
            await context.Reply(CmdPacketType.START_RAID, [1], "anton_phase2_preparing", () => actor.IsCurrent);
            battle.Publish(battle.View);
            return CommandResult.Handled;
        }
        using var publication = context.Sender.QueuePublication(() => actor.IsCurrent);
        int members = party?.Members.Count(m => m.Actor.IsCurrent) ?? 0;
        bool supported = definition?.Type == 0 && start is not null || definition?.Type == 1 && lukeStart is not null;
        bool mayStart = party is not null && supported && party.Leader == actor.Character &&
            (party.Started is not null || members >= definition!.StartMinimum);
        bool creating = mayStart && party!.Started is null;
        using var commands = creating && definition!.Type == 0 ? RaidStartCommands.TryEnter(party!, context.Session) : null;
        using var lukeCommands = mayStart && definition!.Type == 1 ? RaidStartCommandScope.TryEnter(context, party!) : null;
        var check = party is null ? new RaidStartCheck(RaidPackets.StartUnavailable, "no-raid") :
            RaidStartCheck.Evaluate(party.Leader == actor.Character, members, definition);
        var result = !mayStart ? new RaidStartResult(check.Error, check.Reason) : party!.Started is not null && definition!.Type == 0
            ? new RaidStartResult(0, "already-started", Replay: true)
            : definition!.Type == 0
                ? commands is null ? new RaidStartResult(19, "raid-member-command-busy") : start!.Start(actor, party!, definition)
                : lukeCommands is null ? new RaidStartResult(19, "raid-member-command-busy")
                    : lukeStart!.Start(actor, party!, definition, lukeCommands.Parties, lukeCommands.IsCurrent);
        var resources = definition?.Resources;
        log.Write("raid_start_request", new { Connection = context.Id, Self = actor.Character, Key = party?.Key.ToString("X8"), Members = members,
            definition?.StartMinimum, Accepted = result.Error == 0, result.Error, result.Reason, result.Replay,
            Instance = result.Receipt?.Party.Started?.InstanceId, BattleInstance = result.Receipt?.Battle?.Instance,
            BattleRevision = result.Receipt?.Battle?.Revision,
            MissingDungeons = resources?.EntryCatalog?.MissingDungeons.Length, MissingMaps = resources?.EntryCatalog?.MissingMaps.Length,
            MissingFormalDungeons = resources?.MissingFormalDungeons.Length, MissingFormalMaps = resources?.MissingFormalMaps.Length,
            MissingItems = resources?.MissingItems.Length, DefinitionIssues = resources?.Issues.Length,
            TutorialScripts = resources?.Dungeons.Count(d => d.TutorialFileExists),
            DungeonSample = resources?.EntryCatalog is { } entryCatalog ? string.Join(',', entryCatalog.MissingDungeons.Take(8)) : "",
            FormalDungeonSample = resources is null ? "" : string.Join(',', resources.MissingFormalDungeons.Take(8)),
            MapSample = resources is null ? "" : string.Join(',', resources.MissingFormalMaps.Take(8)),
            ItemSample = resources is null ? "" : string.Join(',', resources.MissingItems.Take(8)),
            IssueSample = resources is null ? "" : string.Join(';', resources.Issues.Take(4).Select(i => $"{i.Code}:{i.Path}@{i.TokenIndex}")) });
        if (result.Receipt is { } receipt)
        {
            foreach (var member in receipt.Party.Members)
                member.Actor.Session.RaidStartArrived?.Invoke(receipt, member.Actor);
            registry.PublishDirectory(new(Party: receipt.Party));
        }
        await context.Reply(CmdPacketType.START_RAID, result.Error == 0 ? [1] : RaidPackets.StartFailure(result.Error),
            result.Error == 0 ? "raid_started" : "raid_start_rejected", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> WaitingAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2 || request.Body[0] != 0 || Actor(context) is not { } actor || !MatchesChannel(actor, request.Body[1]))
            return CommandResult.Unhandled;
        byte type = request.Body[1];
        if (catalog.Find(type) is null) return CommandResult.Unhandled;
        ushort? error = registry.AddWaiting(actor, type);
        log.Write("raid_waiting_request", new { Connection = context.Id, Self = actor.Character, RaidType = type, Accepted = error is null, Error = error });
        var replied = context.Reply(CmdPacketType.SET_RAID_WAITING, error is { } code ? RaidPackets.CreateFailure(code) : RaidPackets.WaitingSuccess,
            error is null ? "raid_waiting" : "raid_waiting_rejected", () => actor.IsCurrent);
        if (error is null)
        {
            var rows = registry.List(actor.Channel, type);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, RaidPackets.RaidListBatch(rows.Select(p =>
                (p.Key, p.TitleBytes, p.Type, p.Leader, CharacterName.Encoding.GetBytes(p.LeaderName),
                    p.Members.Single(m => m.Actor.Character == p.Leader).Squad, p.Type == 1 && RaidProjection.CanStart(p, lukeStart is not null))).ToArray()), "raid_recruiting_list",
                () => actor.IsCurrent && registry.IsListSnapshot(actor.Channel, type, rows));
            var waitRows = registry.Waiting(actor.Channel, type);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_WAITING_LIST, RaidPackets.WaitingList(waitRows
                .Select(w => (w.Actor.Character, w.Type)).ToArray()), "raid_waiting_list",
                () => actor.Session.CanPublishRaidWaiting(actor, type, waitRows));
            registry.PublishWaiting(actor);
        }
        await replied;
        return CommandResult.Handled;
    }

    async Task<CommandResult> ModifyAsync(GameConnectionContext context, Packet request)
    {
        ModifyRaidRequest parsed;
        try { parsed = RaidRequests.ModifyRaid(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor) return CommandResult.Unhandled;
        var current = registry.Find(actor);
        var change = registry.Rename(actor, parsed.RaidType, parsed.Name, CharacterName.Encoding.GetBytes(parsed.Name));
        if (change?.After is not { } party)
        {
            ushort error = current is not null && current.Leader != actor.Character ? RaidPackets.ModifyNotLeader : RaidPackets.ModifyNoRaid;
            log.Write("raid_modify_request", new { Connection = context.Id, parsed.RaidType, Accepted = false, Reason = "raid-or-leader-mismatch" });
            await context.Reply(CmdPacketType.MODIFY_RAID_INFO, RaidPackets.CreateFailure(error), "raid_modify_rejected", () => actor.IsCurrent);
            return CommandResult.Handled;
        }
        log.Write("raid_modify_request", new { Connection = context.Id, parsed.RaidType, Accepted = true, Key = party.Key.ToString("X8"), party.Revision });
        foreach (var member in party.Members)
            member.Actor.Session.RaidRecordNoticeArrived?.Invoke(party, member.Actor);
        registry.PublishDirectory(new(Party: party));
        await context.Reply(CmdPacketType.MODIFY_RAID_INFO, RaidPackets.ModifySuccess, "raid_modified", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> CreateAsync(GameConnectionContext context, Packet request)
    {
        CreateRaidRequest parsed;
        try { parsed = RaidRequests.CreateRaid(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        var actor = Actor(context);
        var definition = catalog.Find(parsed.RaidType);
        // raid.lst 没有类型 6：魔界大战的建队走 SET_PARTY_INFO(kind=5)，这里不再为它破例建团本记录。
        var previousWaiting = actor is null ? null : registry.Waiting(actor);
        var party = actor is null || definition is null || !MatchesType(actor, parsed.RaidType) ? null :
            registry.Create(actor, parsed.RaidType, parsed.Name, CharacterName.Encoding.GetBytes(parsed.Name), definition.MemberMax, definition.StartMinimum);
        log.Write("raid_create_request", new { Connection = context.Id, parsed.RaidType, parsed.Name, Accepted = party is not null,
            Reason = party is null ? "raid-admission-or-membership" : null, Key = party?.Key.ToString("X8"), definition?.MemberMax, definition?.StartMinimum });
        if (party is null)
        {
            await context.Reply(CmdPacketType.CREATE_RAID, RaidPackets.CreateFailure(), "raid_create_rejected");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishRaid(actor!, party);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_LIST, RaidProjection.List(party, lukeStart is not null), "raid_created_record", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MODIFY, RaidPackets.OpenWithSquads(party.Key, RaidProjection.Members(party)), "raid_created_open", Guard);
        var replied = context.Reply(CmdPacketType.CREATE_RAID, RaidPackets.CreateSuccess(party.Key), "raid_created",
            () => context.Session.HasRaidKey(actor!, party.Key));
        registry.PublishDirectory(new(Party: party));
        if (previousWaiting is not null) registry.PublishDirectory(new(Waiting: previousWaiting, Removed: true));
        await replied;
        return CommandResult.Handled;
    }

    async Task<CommandResult> LeaveAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2 || Actor(context) is not { } actor) return CommandResult.Unhandled;
        ushort target = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(request.Body);
        var change = start is null ? registry.Leave(actor, target) : start.Leave(actor, target);
        log.Write("raid_leave_request", new { Connection = context.Id, Target = target, Self = actor.Character, Accepted = change is not null,
            Key = change?.Before.Key.ToString("X8"), Leader = change?.After?.Leader, Revision = change?.After?.Revision });
        if (change is null) return CommandResult.Unhandled;
        registry.Publish(change);
        // 踢人成功由被踢者消费ACK665，不能让队长的ACK关掉队长自己的团本窗口。
        if (target == actor.Character)
            await context.Reply(CmdPacketType.LEAVE_RAID, RaidPackets.LeaveSuccess, "raid_left", () => actor.IsCurrent && !context.Session.HasCurrentRaid(actor));
        return CommandResult.Handled;
    }

    async Task<CommandResult> ManagerAsync(GameConnectionContext context, Packet request)
    {
        RaidManagerRequest parsed;
        try { parsed = RaidRequests.Manager(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor || parsed.Character > ushort.MaxValue ||
            parsed.Operation == 0 && parsed.Argument > byte.MaxValue ||
            parsed.Operation == 1 && parsed.Argument != 0 || parsed.Operation > 1)
            return CommandResult.Unhandled;
        // 菜单145「委任团长」在03990155写operation=1、目标u32和argument=0；不猜其它管理操作。
        var change = parsed.Operation == 0
            ? registry.Assign(actor, (ushort)parsed.Character, (byte)parsed.Argument)
            : registry.TransferLeadership(actor, (ushort)parsed.Character);
        log.Write("raid_modify_request", new { Connection = context.Id, parsed.Operation, parsed.Character, Squad = parsed.Argument,
            Accepted = change is not null, Reason = change is null ? "member-squad-or-leader-mismatch" : null,
            Leader = change?.After?.Leader, Revision = change?.After?.Revision });
        if (change is null) return CommandResult.Handled;
        registry.Publish(change);
        await context.Reply(CmdPacketType.RAID_MANAGER_WORK, [1],
            parsed.Operation == 0 ? "raid_squad_changed" : "raid_leader_changed", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> InformationAsync(GameConnectionContext context, Packet request)
    {
        RaidInfoRequest parsed;
        try { parsed = RaidRequests.Information(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (context.Session.CaptureRaidRejoinActor(registry) is not { } actor) return CommandResult.Unhandled;
        var party = registry.Find(parsed.Key, actor.Channel);
        bool accepted = party is not null && MatchesType(actor, party.Type);
        byte[] body = accepted ? RaidPackets.Information(parsed.Mode, party!.Key, party.TitleBytes, party.Type, party.Leader,
            CharacterName.Encoding.GetBytes(party.LeaderName), RaidProjection.Members(party), party.Started is null ? (byte)0 : (byte)2,
            party.Started?.Phase ?? 0, RaidProjection.CanStart(party, lukeStart is not null)) : RaidPackets.InformationFailure(parsed.Mode, parsed.Key);
        await context.Reply(CmdPacketType.REQUEST_RAID_INFO, body, "raid_information", () => actor.IsCurrent && (!accepted || registry.IsCurrent(party!)));
        return CommandResult.Handled;
    }

    public async Task<CommandResult> RequestPeerAsync(GameConnectionContext context, Packet request)
    {
        var parsed = RaidRequests.Peer(request.Body, response: false);
        var actor = Actor(context);
        var other = online?.FindReadyCharacter(parsed.Character)?.CaptureRaidActor(registry);
        var invitation = actor is null || other is null ? null : registry.Invite(actor, other, parsed.Peer);
        log.Write("raid_waiting_request", new { Connection = context.Id, Self = actor?.Character, Target = parsed.Character,
            Accepted = invitation is not null, Reason = invitation is null ? "raid-invitation-unavailable" : null });
        var replied = context.Reply(CmdPacketType.REQUEST_PEER, invitation is null ? PartyPackets.Failure(3, 10) : [1], "raid_peer_request",
            () => actor?.IsCurrent == true);
        if (invitation is not null)
            invitation.Target.Session.RaidPeerNoticeArrived?.Invoke(invitation.Target, invitation.Source, invitation.Peer, true);
        await replied;
        return CommandResult.Handled;
    }

    public async Task<CommandResult> ResponsePeerAsync(GameConnectionContext context, Packet request)
    {
        var parsed = RaidRequests.Peer(request.Body, response: true);
        var actor = Actor(context);
        var result = actor is null ? new RaidJoinResult(false) : registry.Respond(actor, parsed.Character, request.Body.Length == 7, parsed.Peer);
        if (!result.Accepted)
        {
            await context.Reply(CmdPacketType.RESPONSE_PEER, PartyPackets.Failure(3, 10), "raid_peer_response_rejected");
            return CommandResult.Handled;
        }
        var replied = request.Body.Length == 7
            ? context.Reply(CmdPacketType.RESPONSE_PEER, PartyPackets.Response(parsed.Character, 10), "raid_peer_accepted", () => actor!.IsCurrent)
            : Task.CompletedTask;
        if (result.Change is { } change)
        {
            var invitation = result.Invitation!;
            invitation.Source.Session.RaidPeerNoticeArrived?.Invoke(invitation.Source, invitation.Target, parsed.Peer, false);
            registry.Publish(change);
        }
        else if (result.Invitation is { } declined)
            declined.Source.Session.RaidPeerRefused?.Invoke(declined.Source);
        await replied;
        return CommandResult.Handled;
    }

    async Task<CommandResult> EntryCostAsync(GameConnectionContext context, Packet request)
    {
        // 01DC65B0归一化参数为0/1，02173DB0明确传0；自动费用检测另发1，均不代表扣票确认。
        if (request.Body.Length != 1 || request.Body[0] > 1) return CommandResult.Unhandled;
        log.Write("raid_entry_cost_request", new { Connection = context.Id, Flag = request.Body[0] });
        if (Actor(context) is { } actor && registry.Find(actor) is { } party && party.Type == 0 && catalog.Find(party.Type)?.Resources?.Script.EntryCost is { } cost)
        {
            var rows = party.Members.Where(m => m.Actor.IsCurrent).OrderBy(m => m.Slot)
                .Select(m => AntonRaidStart.Cost(m.Actor, cost, party.Started is not null)).ToArray();
            foreach (var member in party.Members.Where(m => m.Actor.IsCurrent))
                member.Actor.Session.RaidCostArrived?.Invoke(rows, member.Actor);
        }
        else context.Sender.Post(0, (ushort)NotiPacketType.RAID_ENTRY_COST_INFO, RaidPackets.EntryCosts([]), "raid_entry_cost_empty");
        await context.Reply(CmdPacketType.RAID_ENTRY_COST_INFO, RaidPackets.EntryCostSuccess, "raid_entry_cost");
        return CommandResult.Handled;
    }
}
