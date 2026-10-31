using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers;

// 正式与诊断实例共用已经确认的选图界面入口，不负责建立副本实例。
internal sealed class DungeonSelectionHandler(TownProjection projection, ServerLog log,
    DfoServer.GameWorld.Characters.TutorialCatalog? tutorials = null,
    DfoServer.GameWorld.Dungeons.DungeonCatalog? dungeons = null,
    DfoServer.Game.Dungeons.PartyDungeonService? partyDungeons = null, TagEvildomProjection? tagEvildom = null, TagEvildomRunProjection? tagRun = null,
    DfoServer.Game.Raids.LukeRaidDungeonService? lukeRaid = null, DfoServer.Game.Raids.AntonRaidDungeonService? antonRaid = null,
    ImperialArenaProjection? imperialArena = null, DfoServer.Game.Raids.AntonRaidDungeonService? preyRaid = null,
    DfoServer.Game.Raids.FiendwarDungeonService? fiendwarRaid = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ENTER_SELECT_DUNGEON, EnterAsync, nameof(DungeonSelectionHandler));
        commands.Register(CmdPacketType.BACK_2_VILLAGE, ReturnAsync, nameof(DungeonSelectionHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CANCEL_QUICK_PARTY, CancelQuickPartyAsync, nameof(DungeonSelectionHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> CancelQuickPartyAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        return await projection.RestoreDungeonSelectionAsync(context, CmdPacketType.CANCEL_QUICK_PARTY)
            ? CommandResult.Handled : CommandResult.Unhandled;
    }

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length == 6 && context.Session.ChannelEntry?.Type == 39 && imperialArena is not null)
        {
            uint imperialDungeon = ImperialArenaRequests.Entry(request.Body);
            // 修理/购券会改主角记录，但已装的双Actor只装备用角色；失效时就地重建一次，不再要求重登。
            if (!context.BlocksTownActions) await imperialArena.RefreshPairAsync(context);
            var selected = !context.BlocksTownActions && dungeons is not null
                ? context.Session.BeginImperialArenaSelection(imperialDungeon, dungeons, imperialArena.Catalog, imperialArena.Store) : null;
            log.Write(selected is null ? "imperial_arena_entry_rejected" : "imperial_arena_selection_opened",
                new { Connection = context.Id, Dungeon = imperialDungeon, Reason = selected is null ? context.Session.ImperialArenaEntryReject : null,
                    selected?.SelectionId });
            if (selected is null)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "imperial_arena_entry_rejected");
            else if (await projection.DetachForDungeonSelectionAsync(context, selected))
            {
                bool Guard() => context.Session.CanPublishDungeonSelection(selected);
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "imperial_arena_selection_opened", Guard);
                await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(selected.Character.Id), "imperial_arena_selection_state", Guard);
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0), "imperial_arena_selection_scene", Guard);
            }
            return CommandResult.Handled;
        }
        if (request.Body.Length == 6 && context.Session.ChannelEntry?.Type == 53 && tagEvildom is not null &&
            context.Session.Selected?.Position is { Town: 62, Area: 0 })
        {
            uint tagDungeon = TagEvildomRequests.Entry(request.Body);
            // 采购、修理与体力恢复会改主角记录；已装的双Actor失效时就地重建一次，不再要求重登。
            if (!context.BlocksTownActions) await tagEvildom.RefreshPairAsync(context);
            // 结构与资格分开；重置后旧6B15明确拒绝，不把合法TAG正文落入普通4B断线分支。
            var selected = !context.BlocksTownActions && dungeons is not null && tagRun is not null
                ? context.Session.BeginTagEvildomSelection(tagDungeon, dungeons, tagRun.Rewards, tagEvildom.Store) : null;
            log.Write(selected is null ? "tag_evildom_entry_rejected" : "tag_evildom_selection_opened",
                new { Connection = context.Id, Dungeon = tagDungeon, Reason = selected is null ? context.Session.TagEvildomEntryReject : null,
                    selected?.SelectionId });
            if (selected is null)
            {
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "tag_evildom_entry_rejected");
                await context.Notify(NotiPacketType.AAS_TIPS, ServerNoticePackets.Build("再战魔界",
                    "未进入：请确认当前已选难度、两名95级角色资料和当日/每周次数。资料变更后请以编队第一名重新登录。"), "tag_evildom_entry_notice");
            }
            else if (await projection.DetachForDungeonSelectionAsync(context, selected))
            {
                bool Guard() => context.Session.CanPublishDungeonSelection(selected);
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "tag_evildom_selection_opened", Guard);
                await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(selected.Character.Id), "tag_evildom_selection_state", Guard);
                // solo TAG没有705入队旁路；先27建立scene2，随后28/29才能进入原生scene23。
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0), "tag_evildom_selection_scene", Guard);
            }
            return CommandResult.Handled;
        }
        var entry = DungeonRequests.ParseEntry(request.Body);
        if (!context.Session.CheckState(UserStateRequirement.WorldReady)) return CommandResult.Unhandled;
        if (context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        if (context.Session.ChannelEntry?.Type == 45 && fiendwarRaid is not null)
        {
            DfoServer.Game.Dungeons.PartyDungeonNotice[] selected = [];
            string? reason = "fiendwar-raid-required";
            if (entry.DungeonIndex <= int.MaxValue && fiendwarRaid.Find(context.Session) is { } raid)
            {
                var existing = context.Session.CurrentParty?.Invoke();
                using var commands = existing is not null && context.DispatchGates?.Holds(context, existing.Commands) == true
                    ? await PartyDungeonProjection.AcquirePeersAsync(context, existing.Members)
                    : DfoServer.Game.Raids.RaidStartCommands.TryEnter(fiendwarRaid.EncounterMembers(context.Session, raid), context.Session);
                if (commands is not null)
                {
                    var party = context.Session.Phase == SessionPhase.Town
                        ? fiendwarRaid.Group(context.Session, (int)entry.DungeonIndex, out reason) : context.Session.CurrentParty?.Invoke();
                    if (party is not null) selected = fiendwarRaid.Select(context.Session, (int)entry.DungeonIndex, party, out reason,
                        replay: context.Session.Phase == SessionPhase.Loading);
                }
                else reason = "fiendwar-member-command-busy";
            }
            log.Write("fiendwar_selection", new { Connection = context.Id, Dungeon = entry.DungeonIndex, Members = selected.Length, Reason = reason });
            if (selected.Length == 0)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "fiendwar_selection_rejected");
            else await PartyDungeonProjection.PublishAsync(selected);
            return CommandResult.Handled;
        }
        var raidService = context.Session.ChannelEntry?.Type == 50 ? preyRaid : antonRaid;
        if (entry.DungeonIndex != 0 && context.Session.ChannelEntry?.Type is 23 or 50 && raidService is { Enabled: true } &&
            (context.Session.StartedRaid is not null || entry.DungeonIndex <= int.MaxValue && raidService.Contains((int)entry.DungeonIndex)))
        {
            var party = context.Session.CurrentParty?.Invoke();
            string? reason = "anton-entry-party-dispatch-gate-mismatch";
            DfoServer.Game.Dungeons.PartyDungeonNotice[] selected = [];
            if (party is not null && context.DispatchGates?.Holds(context, party.Commands) == true)
            {
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
                if (entry.DungeonIndex <= int.MaxValue)
                    selected = raidService.Select(context.Session, (int)entry.DungeonIndex, party, out reason,
                        replayLoading: context.Session.Phase == SessionPhase.Loading);
            }
            log.Write("dungeon_entry", new { Connection = context.Id, Accepted = selected.Length != 0, Party = true,
                RaidType = 0, Replay = selected.Any(notice => notice.Kind == DfoServer.Game.Dungeons.PartyDungeonNoticeKind.LoadingReplay),
                Dungeon = entry.DungeonIndex, Members = selected.Length, RejectReason = reason, MissingMaps = raidService.MissingMaps });
            if (selected.Length == 0)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "anton_selection_rejected");
            else if (selected is [{ Kind: DfoServer.Game.Dungeons.PartyDungeonNoticeKind.LoadingReplay, Scene: { } antonReplay }])
                // 与首次party_selection_opened及普通continuation.Replay分支相同的ACK15；不重发选图状态/27。
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "anton_loading_selection_replay",
                    () => context.Session.CanPublishFormalDungeon(antonReplay));
            else await PartyDungeonProjection.PublishAsync(selected);
            return CommandResult.Handled;
        }
        if (context.Session.ChannelEntry?.Type == 54 && context.Session.Selected?.Position is { Town: 67, Area: 2 })
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, context.Session.CurrentParty?.Invoke()?.Members ?? []);
            var selected = partyDungeons?.BeginSelection(context.Session, evildomDungeon: entry.DungeonIndex) ?? [];
            log.Write("evildom_war_selection", new { Connection = context.Id, Dungeon = entry.DungeonIndex, Accepted = selected.Length > 0,
                Members = selected.Length });
            if (selected.Length == 0)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "evildom_war_selection_rejected");
            else await PartyDungeonProjection.PublishAsync(selected);
            return CommandResult.Handled;
        }
        if (context.Session.ChannelEntry?.Type == 32 &&
            context.Session.StartedRaid is not null && context.Session.Phase is SessionPhase.Town or SessionPhase.MapSelect)
        {
            var selectedParty = context.Session.CurrentParty?.Invoke();
            if (selectedParty is null || context.DispatchGates?.Holds(context, selectedParty.Commands) != true)
            {
                log.Write("dungeon_entry", new { Connection = context.Id, Accepted = false, Party = true,
                    RaidType = 1, Dungeon = entry.DungeonIndex, RejectReason = "luke-party-dispatch-gate-mismatch" });
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "luke_selection_rejected");
                return CommandResult.Handled;
            }
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, selectedParty.Members);
            if (entry.DungeonIndex == 0)
            {
                // 这里只打开原生团本选图；图号、路线及成员资格在16经领域服务一起确认。
                var opened = !context.BlocksTownActions ? lukeRaid?.OpenSelection(context.Session, selectedParty) ?? [] : [];
                log.Write("dungeon_entry", new { Connection = context.Id, Accepted = opened.Length > 0, Party = true,
                    RaidType = 1, Dungeon = 0, Reason = "luke-selection-open" });
                if (opened.Length == 0)
                    await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "luke_selection_rejected");
                else await PartyDungeonProjection.PublishAsync(opened);
                return CommandResult.Handled;
            }
            lukeRaid?.CancelSelection(context.Session, selectedParty, preserveScene: true);
            var selected = entry.DungeonIndex <= int.MaxValue && !context.BlocksTownActions
                ? lukeRaid?.Select(context.Session, (int)entry.DungeonIndex, selectedParty) ?? [] : [];
            log.Write("dungeon_entry", new { Connection = context.Id, Accepted = selected.Length > 0, Party = true,
                RaidType = 1, Dungeon = entry.DungeonIndex, Members = selected.Length });
            if (selected.Length == 0)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "luke_selection_rejected");
            else await PartyDungeonProjection.PublishAsync(selected);
            return CommandResult.Handled;
        }
        if (context.Session.HasMultiplePartyMembers?.Invoke() == true && context.Session.Phase is SessionPhase.Town or SessionPhase.MapSelect)
        {
            if (partyDungeons is null) return CommandResult.Unhandled;
            if (entry.DungeonIndex != 0)
            {
                var party = context.Session.CurrentParty?.Invoke();
                if (dungeons is not null && context.Session.Phase == SessionPhase.Town &&
                    context.Session.CanBeginSpecifiedPartySelection(entry.DungeonIndex, dungeons))
                {
                    using var couplePeers = await PartyDungeonProjection.AcquirePeersAsync(context, party?.Members ?? []);
                    var coupleSelection = partyDungeons.BeginSelection(context.Session);
                    if (coupleSelection.Length == 0)
                        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "party_direct_selection_rejected");
                    else await PartyDungeonProjection.PublishAsync(coupleSelection.Select(notice => notice with { Option = 1 }));
                    // 已选定目标与客户端scene2是两层状态；由共用投影补空27，不改写请求的副本编号。
                    return CommandResult.Handled;
                }
                if (party is null || context.Session.Phase != SessionPhase.MapSelect ||
                    !party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, context.Session)))
                {
                    await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "party_direct_selection_unavailable");
                    return CommandResult.Handled;
                }
                var confirmation = context.Session.BeginDungeonSelection(partyAdmission: true);
                if (confirmation is not null)
                    await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "party_dungeon_chosen",
                        () => context.Session.CanPublishDungeonSelection(confirmation));
                return CommandResult.Handled;
            }
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, context.Session.CurrentParty?.Invoke()?.Members ?? []);
            var selected = partyDungeons.BeginSelection(context.Session);
            if (selected.Length > 0 && dungeons is not null)
            {
                int target = context.Session.PrepareLegendDungeonSelection(dungeons);
                selected = selected.Select(n => n with { LegendDungeon = target }).ToArray();
            }
            log.Write("dungeon_entry", new { Connection = context.Id, Accepted = selected.Length > 0, Party = true,
                Members = selected.Length, PreservePartyActors = selected.Length > 0 });
            if (selected.Length == 0)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "party_selection_rejected");
            else await PartyDungeonProjection.PublishAsync(selected);
            return CommandResult.Handled;
        }
        if (context.Session.PartyDungeon is { } hellParty)
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context,hellParty.Members);
            var selected = hellParty.ClearedHellSelection(context.Session,entry.DungeonIndex);
            if (selected.Length == 0) return CommandResult.Unhandled;
            int target = dungeons is null ? 0 : context.Session.PrepareLegendDungeonSelection(dungeons);
            log.Write("formal_hell_selection",new { Connection=context.Id, Party=hellParty.Id,
                entry.DungeonIndex, Members=selected.Length, LegendDungeon=target });
            await PartyDungeonProjection.PublishAsync(selected.Select(n => n with { LegendDungeon=target }));
            return CommandResult.Handled;
        }
        if (context.Session.Phase == SessionPhase.Dungeon ||
            context.Session.Phase == SessionPhase.Settlement && context.Session.FormalDungeonResultFocus() is { Entry.IsCosmo: true })
        {
            var hellSelection = context.Session.BeginClearedHellSelection(entry.DungeonIndex);
            if (hellSelection is null) return CommandResult.Unhandled;
            int target = dungeons is null ? 0 : context.Session.PrepareLegendDungeonSelection(dungeons);
            log.Write("formal_hell_selection", new { Connection = context.Id, hellSelection.Character.Id, hellSelection.SelectionId,
                entry.DungeonIndex, LegendDungeon = target });
            if (!await projection.DetachForDungeonSelectionAsync(context, hellSelection)) return CommandResult.Handled;
            bool Guard() => context.Session.CanPublishDungeonSelection(hellSelection);
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "formal_hell_selection_opened", Guard);
            await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(hellSelection.Character.Id), "formal_hell_selection_state", Guard);
            await GriefTowerProjection.SelectionAsync(context,dungeons,target,"formal_hell_select_notice",Guard);
            return CommandResult.Handled;
        }
        var continuation = context.Session.CompleteFormalDungeonContinuation(entry.DungeonIndex);
        if (continuation.Pending && continuation.Selection is { } continuationSelection)
        {
            if (!await projection.DetachForDungeonSelectionAsync(context,continuationSelection)) return CommandResult.Handled;
            bool Guard() => context.Session.CanPublishDungeonSelection(continuationSelection);
            log.Write("formal_quest_continuation_selection",new { Connection = context.Id, entry.DungeonIndex,
                continuation.Opened, continuationSelection.SelectionId });
            // 27隐藏旧Scene界面并切换场景模块；ACK15/state1不能替代，缺少它会保留旧结算页。
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_entry_confirm",Guard);
            if (continuation.Opened)
            {
                await context.Notify(NotiPacketType.USER_STATE,DungeonPackets.DungeonPlayerState(continuationSelection.Character.Id),"formal_next_quest_entry_state",Guard);
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionNotice(0, despairLayer: context.Session.DespairSelectionLayer()),"formal_next_quest_scene_transition",Guard);
            }
            return CommandResult.Handled;
        }
        if (continuation.Pending && continuation.Replay is { } replay)
        {
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"formal_next_quest_entry_replay",
                () => context.Session.CanPublishFormalDungeon(replay));
            return CommandResult.Handled;
        }
        if (continuation.Pending)
        {
            // 连图无法继续时不能静默吞掉15：城镇内退回普通选图，其余状态明确拒绝，客户端不能停在旧副本。
            log.Write("formal_quest_continuation_closed",new { Connection = context.Id, continuation.Cancelled, continuation.Reason,
                entry.DungeonIndex });
            bool fallback = context.Owner.Execute(() => continuation.Cancelled && !context.BlocksTownActions &&
                context.Session.Ready && context.Session.Phase == SessionPhase.Town);
            if (!fallback)
            {
                var before = context.Owner.Execute(() => (context.Session.Ready,context.Session.SelectionEpoch,context.Session.Phase));
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionUnavailable(),"formal_next_quest_entry_rejected",
                    () => context.Owner.Execute(() => before.Ready && context.Session.Ready &&
                        context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase));
                return CommandResult.Handled;
            }
        }
        var replayBefore = context.Owner.Execute(() => (context.Session.Selected,context.Session.Ready,
            context.Session.SelectionEpoch,context.Session.Phase,Identity: context.Session.DungeonSnapshot?.Identity));
        var questReplaySelection = context.Session.BeginQuestReplayDungeonSelection(entry.DungeonIndex);
        if (questReplaySelection.Pending)
        {
            if (questReplaySelection.Receipt is { } selected)
            {
                if (!context.Session.ConfirmNewDungeonSelection(selected)) return CommandResult.Handled;
                if (!await projection.DetachForDungeonSelectionAsync(context,selected)) return CommandResult.Handled;
                bool Guard() => context.Session.CanPublishDungeonSelection(selected);
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"quest_replay_selection_opened",Guard);
                await context.Notify(NotiPacketType.USER_STATE,DungeonPackets.DungeonPlayerState(selected.Character.Id),"quest_replay_selection_state",Guard);
            }
            else if (questReplaySelection.Replay is { } installed)
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionOpened(),"quest_replay_selection_retry",
                    () => context.Session.CanPublishFormalDungeon(installed));
            else
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON,DungeonPackets.SelectionUnavailable(),"quest_replay_selection_rejected",
                    () => context.Owner.Execute(() => replayBefore.Ready && context.Session.Ready &&
                        context.Session.SelectionEpoch == replayBefore.SelectionEpoch && context.Session.Phase == replayBefore.Phase &&
                        ReferenceEquals(context.Session.Selected,replayBefore.Selected) && context.Session.DungeonSnapshot?.Identity == replayBefore.Identity));
            return CommandResult.Handled;
        }
        if (context.BlocksTownActions)
            return CommandResult.Unhandled;
        var receipt = entry.DungeonIndex == 7142 ? context.Session.BeginClassExperienceSelection() :
            tutorials is not null ? context.Session.BeginTutorialSelection(entry.DungeonIndex, tutorials) : null;
        if (entry.DungeonIndex == 7142 && receipt is null) return CommandResult.Unhandled;
        receipt ??= context.Session.BeginDungeonSelection();
        if (receipt is null)
        {
            log.Write("dungeon_entry", new { Connection = context.Id, Accepted = false, Dungeon = entry.DungeonIndex,
                Reason = "gate-return-position-unavailable", Position = replayBefore.Selected?.Position });
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionUnavailable(), "dungeon_entry_rejected",
                () => context.Owner.Execute(() => replayBefore.Ready && context.Session.Ready &&
                    context.Session.SelectionEpoch == replayBefore.SelectionEpoch && context.Session.Phase == replayBefore.Phase &&
                    ReferenceEquals(context.Session.Selected,replayBefore.Selected) && context.Session.DungeonSnapshot?.Identity == replayBefore.Identity));
            return CommandResult.Handled;
        }
        if (!context.Session.ConfirmNewDungeonSelection(receipt)) return CommandResult.Handled;
        await EkernCycleProjection.RefreshAsync(context, () => context.Session.CanPublishDungeonSelection(receipt));
        int legendTarget = dungeons is null ? 0 : context.Session.PrepareLegendDungeonSelection(dungeons);
        if (!await projection.DetachForDungeonSelectionAsync(context, receipt))
            return CommandResult.Handled;
        bool CanSend() => context.Session.CanPublishDungeonSelection(receipt);
        // 正文 u32 为 0 是左侧入口打开选图；非 0 是已经选中的副本编号。再推空列表会把选图界面重新打开。
        uint dungeon = entry.DungeonIndex;
        log.Write("dungeon_entry", new { Connection = context.Id, Accepted = true, Dungeon = dungeon, Reason = dungeon == 0 ? "select-notice" : "dungeon-chosen", receipt.Character.Position, receipt.SelectionId,
            LegendDungeon = legendTarget });
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "dungeon_entry_opened", CanSend);
        // 同步客户端选图状态，触发旧城镇实体清理；仅切服务端阶段不会结束客户端旧动作。
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "dungeon_selection_state", CanSend);
        // #233 实验：打开选图前刷新强袭模式每日信息（跨日后客户端不会自行更新今日索引）。
        if (dungeon == 0)
        {
            await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO, AssaultModePackets.Today(TimeProvider.System, context.Session.AssaultDailyCounts(TimeProvider.System.GetLocalNow())), "assault_mode_daily_info", CanSend);
            // 仅刷新专属魔兽选择界面的五项；普通地图不接收同号魔兽次数，时钟沿Session准入。
            foreach (var count in context.Session.BeastSelectionCounts())
            {
                await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Info(count.Dungeon, count.Remaining, 0),
                    "beast_dungeon_daily_count", CanSend);
            }
        }
        if (dungeon == 0 || receipt.Tutorial && receipt.OpensClientSelector)
            await GriefTowerProjection.SelectionAsync(context,dungeons,legendTarget,"dungeon_select_notice",CanSend);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.IsSelectionReturn(request.Body) || context.BlocksTownActions)
            return CommandResult.Unhandled;
        if (context.Session.CurrentParty?.Invoke() is { Members.Length: > 1 } party)
        {
            if (!party.Members.Any(m => m.Slot == party.Leader && ReferenceEquals(m.Actor.Session, context.Session)))
                return CommandResult.Handled;
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
            var returned = partyDungeons?.ReturnSelection(context.Session) ?? [];
            await PartyDungeonProjection.PublishAsync(returned);
            return CommandResult.Handled;
        }
        var receipt = context.Session.ReturnFromDungeonSelection();
        log.Write("dungeon_selection_return", new { Connection = context.Id, Accepted = receipt is not null, receipt?.SelectionId });
        if (receipt is not null)
            await projection.ReturnFromDungeonSelectionAsync(context, receipt);
        return CommandResult.Handled;
    }
}
