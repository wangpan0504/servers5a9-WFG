using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    public void PreparePartyContext(GameConnectionContext context)
    {
        context.Session.PartyDungeonNoticeArrived = notice => PublishPartyNoticeAsync(context, notice);
        if (partyDungeons is not null) context.LukePhaseExit = new(context, partyDungeons, town, log, lukeLifecycle);
    }

    static Task PublishPartyHostAsync(GameConnectionContext context, PartyMember[] members, Func<bool> guard)
    {
        byte? slot = context.Session.PartyDungeon?.Leader ?? context.Session.CurrentParty?.Invoke()?.Leader;
        if (slot is null || !members.Any(m => m.Slot == slot))
            throw new InvalidOperationException("组队场景缺少当前主机成员");
        return context.Notify(NotiPacketType.UDP_HOST, PartyPackets.Host(slot.Value), "party_dungeon_host", guard);
    }

    async Task PublishPartyRosterAsync(GameConnectionContext context, PartyMember[] members, Func<bool> guard)
    {
        foreach (var member in members.OrderBy(m => ReferenceEquals(m.Actor.Session, context.Session) ? 0 : 1).ThenBy(m => m.Slot))
        {
            if (member.Actor.Session.PublicCharacter(member.Actor.Character.Id) is not { } identity) continue;
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(identity.Character, identity.PvpGrade, identity.Channel, adventureName: identity.AdventureName,
                    sceneState: ActorSceneState.Dungeon, premiumPcRoom: identity.PremiumPcRoom), "party_dungeon_actor", guard);
            // 0267C569读取角色ID，0267C581按ID查找已安装Actor；完整属性包同样适用于当前副本队员。
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Player(identity.Character, (partyStats ?? throw new InvalidOperationException("组队副本缺少属性目录")).Compute(identity.Character),
                    identity.AdventureLevel, identity.Channel), "party_dungeon_player", guard);
        }
        var endpoints = PartyPackets.Endpoints(members.Where(m => m.Actor.Session.PartyEndpoint is not null)
            .Select(m => new PartyEndpointRow(m.Actor.Character.Id, m.Actor.Session.PartyEndpoint!, m.Actor.Session.PeerAccount)).ToArray());
        // 名册处理会立即按Actor端点创建peer；必须先恢复地址和账号，避免以0.0.0.0/账号0启动连接。
        await context.Notify(NotiPacketType.USER_UDP_IP_PORT, endpoints, "party_dungeon_endpoints_before_roster", guard);
        // 进房等路径可能已清空队伍槽；名册绑定后再恢复端点和实时信息，选图则沿用原成员对象。
        var party = context.Session.CurrentParty?.Invoke() ?? throw new InvalidOperationException("组队场景缺少当前队伍名册");
        var channel = members[0].Identity.Channel;
        await context.Notify(NotiPacketType.PARTY_INFO,
            PartyPackets.Information(party.Id, channel.ServerIndex, checked((byte)channel.Number),
                members.Select(m => (m.Slot, m.Actor.Character.Id)).ToArray(), party.Leader, false, party.Settings), "party_dungeon_roster", guard);
        await context.Notify(NotiPacketType.USER_UDP_IP_PORT, endpoints, "party_dungeon_endpoints", guard);
        await context.Notify(NotiPacketType.PARTY_MEMBER_REALTIME_INFO,
            PartyPackets.Realtime(members.Select(m => (m.Actor.Character.Id, m.Slot)).ToArray()), "party_dungeon_realtime", guard);
    }

    async Task PublishPartyNoticeAsync(GameConnectionContext context, PartyDungeonNotice notice)
    {
        if (notice.Kind == PartyDungeonNoticeKind.FiendwarTitles)
        {
            if (titles is null || titleProjection is null || inventoryProjection is null) return;
            var result = context.Session.UpdateTitleAchievements(titles, fiendwarOnly: true);
            await titleProjection.PublishAsync(context, result, inventoryProjection);
            if (result.Plan is { Changed: true }) log.Write("fiendwar_title_facts_applied", new
                { Connection = context.Id, Character = result.Receipt?.Character.Id, Quests = result.Plan.FiendwarFacts.Select(row => row.Quest).ToArray() });
            return;
        }
        if (lukeLifecycle is not null && notice.Kind is PartyDungeonNoticeKind.RaidBattleSync or PartyDungeonNoticeKind.Departure &&
            context.Session.CommitPendingLukePhase(lukeLifecycle))
            log.Write("raid_phase_committed", new { Connection = context.Id, RaidType = context.Session.RaidCombatKind(), Instance = context.Session.RaidCombatView()?.Instance });
        var hiddenReward = notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated && notice.WarView is { } hiddenView &&
            context.Session.CanPublishEvildomWar(hiddenView) && evildomRewards is not null && evildomRewardStore is not null
            ? context.Session.CommitEvildomWarHiddenReward(evildomRewards, evildomRewardStore) : null;
        // 奖励规划和持久化先于发送队列预留；只消费当前战役真实完成事实。
        var warReward = notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated && notice.WarView is { Completed: true } completed &&
            context.Session.CanPublishEvildomWar(completed) && evildomRewards is not null && evildomRewardStore is not null
            ? context.Session.CommitEvildomWarReward(evildomRewards, evildomRewardStore) : null;
        using var publication = context.Sender.QueuePublication(notice.Recipient.Actor.IsCurrent);
        if (notice.Kind is PartyDungeonNoticeKind.EvildomWarStarted or PartyDungeonNoticeKind.EvildomWarUpdated)
        {
            if (notice.WarView is not { } view || !context.Session.CanPublishEvildomWar(view)) return;
            if (notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated)
                log.Write("evildom_war_progress", new { Connection = context.Id, view.BattleId, view.Revision,
                    view.Completed, States = view.Dungeons.Select(row => new { row.Id, row.State }).ToArray() });
            await context.Notify(NotiPacketType.EVILDOM_WAR_INFO, EvildomWarPackets.Info(view.Mode,
                (int)(view.Deadline - context.Session.EvildomWarNow).TotalSeconds, view.Dungeons),
                "evildom_war_info", () => context.Session.CanPublishEvildomWar(view));
            if (hiddenReward is not null)
            {
                if (!hiddenReward.Commit.MailIds.IsEmpty)
                    await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "evildom_war_hidden_reward_mail",
                        () => context.Session.CanPublishEvildomWarHiddenReward(hiddenReward));
                log.Write("evildom_war_hidden_reward_committed", new { Connection = context.Id, view.BattleId,
                    Character = hiddenReward.Admission.CharacterId, hiddenReward.Commit.Duplicate,
                    MailIds = hiddenReward.Commit.MailIds, Rule = EvildomWarRewardPolicy.HiddenRule });
            }
            if (warReward is not null)
            {
                bool sent = false;
                bool Guard() => sent = context.Session.CanPublishEvildomWarReward(warReward);
                if (context.Session.ShouldPublishEvildomWarReward(warReward))
                {
                    var groups = warReward.Commit.Plan.Reward.Items.GroupBy(row => row.Group).Select(group =>
                        new EvildomWarPackets.RewardGroup(group.Key, group.Select(row =>
                            new EvildomWarPackets.RewardRow(0, row.ItemId, checked((uint)row.Count))).ToArray())).ToArray();
                    // 首字段和特殊展示类型的业务语义未闭合；本服只用通用物品图标及真实数量，首字段保留0。
                    await context.Notify(NotiPacketType.EVILDOM_WAR_REWARD_LIST, EvildomWarPackets.Rewards(0, groups),
                        "evildom_war_reward", Guard);
                    if (sent) context.Session.ConfirmEvildomWarRewardPublished(warReward);
                }
                if (!warReward.Commit.MailIds.IsEmpty)
                    await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "evildom_war_reward_mail", Guard);
                log.Write("evildom_war_reward_committed", new { Connection = context.Id, view.BattleId,
                    Character = warReward.Admission.CharacterId, warReward.Commit.Duplicate,
                    MailIds = warReward.Commit.MailIds, Rule = EvildomWarRewardPolicy.Rule });
            }
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.RaidBattleSync)
        {
            if (notice.RaidBattleChange is { } change)
                log.Write(notice.RaidEvent is null ? "raid_run_clear" : "raid_run_event", new { Connection = context.Id,
                    Event = notice.RaidEvent?.ToString(), RaidType = context.Session.RaidCombatKind(), Instance = change.After.Instance,
                    Run = notice.Scene?.Snapshot.Identity.InstanceId, Dungeon = notice.Scene?.Entry.DungeonId,
                    Members = notice.Members.Length, change.After.Phase, BeforeRevision = change.Before.Revision,
                    AfterRevision = change.After.Revision, State = change.After.State.ToString() });
            context.Session.PublishRaidCombat();
            return;
        }
        if (notice.RaidBattleChange is not null && notice.Kind is not (PartyDungeonNoticeKind.Departure or PartyDungeonNoticeKind.ResultReturn))
            context.Session.PublishRaidCombat();
        if (notice.Kind == PartyDungeonNoticeKind.Departure && notice.Travel is { } departure)
        {
            if (context.Death is not null) await context.Death.StopAsync();
            await town.ReturnFromFormalDungeonAsync(context, departure, "party_departure_town");
            log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "party_departure",
                departure.Character.Id, departure.Character.Position });
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.AntonHp)
        {
            if (notice.AntonHp is { } hp && notice.Scene is { } hpScene && context.Session.PartyDungeon is { } hpRun)
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_MONSTER_HP, RaidCombatPackets.AntonHp(hp),
                    "anton_monster_hp", () => notice.Recipient.IsCurrent() && ReferenceEquals(context.Session.PartyDungeon, hpRun) &&
                        hpRun.IsAntonHpCurrent(hp) && context.Session.CanPublishFormalDungeon(hpScene) && context.Session.RaidCombatView() is { State: DfoServer.Game.Raids.AntonRaidBattleState.Fighting, PhaseExpired: false });
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.MemberLeft)
        {
            await context.Notify(NotiPacketType.USER_LEAVE, TownPackets.RemoveActor(notice.Peer), "party_dungeon_member_left");
            GuildDungeonPartyBuffProjection.Post(context);
            await PublishPartyHostAsync(context, notice.Members, () => notice.Recipient.IsCurrent() &&
                context.Session.PartyDungeon is { } instance && instance.Members.Contains(notice.Recipient));
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.SelectionReturn && notice.Selection is { } returning)
        {
            await town.ReturnFromDungeonSelectionAsync(context, returning);
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.ResultReturn && notice.Travel is { } travel)
        {
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(1, (byte)DungeonResultOption.ReturnTown),
                "party_result_return", () => context.Session.CanPublishTravel(travel));
            await town.ReturnFromFormalDungeonAsync(context, travel, "party_result_town");
            return;
        }
        if (notice.Kind is PartyDungeonNoticeKind.RaidSelection or PartyDungeonNoticeKind.Selection or PartyDungeonNoticeKind.ResultSelection && notice.Selection is { } selection)
        {
            IReadOnlySet<ushort>? preservePartyActors = notice.Kind is PartyDungeonNoticeKind.Selection or PartyDungeonNoticeKind.RaidSelection
                ? notice.Members.Select(member => member.Actor.Character.Id).ToHashSet() : null;
            if (!await town.DetachForDungeonSelectionAsync(context, selection, preservePartyActors)) return;
            bool Guard() => context.Session.CanPublishDungeonSelection(selection);
            await EkernCycleProjection.RefreshAsync(context,Guard);
            if (notice.Kind == PartyDungeonNoticeKind.ResultSelection)
                await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(1, (byte)DungeonResultOption.OtherDungeon), "party_result_selection", Guard);
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "party_selection_opened", Guard);
            await PublishPartyRosterAsync(context, notice.Members, Guard);
            foreach (var member in notice.Members)
                await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(member.Actor.Character.Id), "party_selection_state", Guard);
            await PublishPartyHostAsync(context, notice.Members, Guard);
            RaidCombatProjection.Selection(context, Guard);
            // 与修炼场指定直入、TAG/帝国15相同：城镇直入先发42B空27建立scene2，585之后、28/29之前；已在选图的不重复打开。
            if (notice.Kind == PartyDungeonNoticeKind.RaidSelection && notice.SceneTransition)
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(), "raid_direct_selection_scene", Guard);
            // 指定副本的非零15仍需27建立scene2；空列表不改变已选目标，不能省略客户端上下文。
            if (notice.Kind == PartyDungeonNoticeKind.Selection && notice.Option != 0 && notice.SceneTransition)
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(), "party_direct_selection_scene", Guard);
            // 86结果选图语义映射至95已有27场景边界，不把团本选择投影为普通副本列表。
            if (notice.Kind == PartyDungeonNoticeKind.ResultSelection && notice.SceneTransition)
                await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(), "anton_result_selection_scene", Guard);
            if (notice.Kind != PartyDungeonNoticeKind.RaidSelection && notice.Option == 0)
                await GriefTowerProjection.SelectionNoticeAsync(context, notice.LegendDungeon, "party_selection_notice", Guard);
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.Death && notice.Death is { } death)
        {
            await PublishDeathAsync(context, death, notice.Previous, death.MonsterKey, new { Party = true });
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.Pickup)
        {
            await PublishPartyPickupAsync(context, notice);
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.Passive && notice.QuestScene is { } progress)
        {
            if (progress.Commit is { ProjectionCurrent: false }) { context.Disconnect(); return; }
            bool Guard() => context.Session.CanPublishQuestLifecycle(progress);
            if (progress.Commit is not null)
            {
                if (progress.Commit.Plan.Inventory.ChangedSlots.Length > 0)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(progress.Character.Inventory,
                        InventoryListType.Main,[.. progress.Commit.Plan.Inventory.ChangedSlots]),"party_passive_items",Guard);
                await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(progress.Character.ActiveMainQuests),
                    "party_passive_quests",Guard);
                await DungeonSettlementProjection.EnableAsync(context);
            }
            return;
        }
        if (notice.Scene is not { } scene) return;
        if (notice.Kind == PartyDungeonNoticeKind.RetryEligibility)
        {
            await DungeonSettlementProjection.RefreshCosmoRetryAsync(context);
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.Cinematic)
        {
            await DungeonSettlementProjection.EnableAsync(context);
            return;
        }
        bool published = false;
        bool Current() => published = context.Session.CanPublishFormalDungeon(scene);
        if (notice.Kind == PartyDungeonNoticeKind.Life)
        {
            await context.Notify(NotiPacketType.DIE_STATE, DungeonPackets.CharacterLife(notice.Peer, notice.State), "party_character_life", Current);
            return;
        }
        if (notice.Kind == PartyDungeonNoticeKind.Pause)
        {
            await context.Notify(NotiPacketType.DUNGEON_EVENT_STORY_PAUSE, DungeonPackets.StoryPause(notice.Peer, notice.State, notice.Option),
                "party_story_pause", Current);
            if (notice.State == 1)
                await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(notice.Peer), "party_story_player_state", Current);
            return;
        }
        switch (notice.Kind)
        {
            case PartyDungeonNoticeKind.LoadingReplay:
                // 现场00:02:51的28/29及首次Entry构包合同；重用冻结seed/演员，不补无证ACK16。
                await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(scene.Entry), "anton_loading_information_replay", Current);
                await context.Notify(NotiPacketType.START_MAP,
                    DungeonPackets.StartMap(scene.Entry, scene.Monsters, scene.MapSeed, passiveItems: scene.PassiveItems), "anton_loading_map_replay", Current);
                RaidCombatProjection.AntonTimersForScene(context, Current);
                break;
            case PartyDungeonNoticeKind.Entry:
                await town.DetachAsync(context);
                GuildDungeonPartyBuffProjection.Post(context, clear: true);
                if (scene.Entry.Hope is { } hope)
                    await context.Reply(CmdPacketType.ACCEPT_QUEST, QuestPackets.Accepted(hope.Acceptance.QuestId,1,[]),
                        "party_operation_hope_quest_assigned",Current);
                if (notice.State == 1)
                {
                    await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(1, (byte)DungeonResultOption.Retry), "party_result_retry", Current);
                    await PublishPartyHostAsync(context, notice.Members, Current);
                    await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(), "party_retry_transition", Current);
                }
                if (scene.Entry.RaidEntry?.Kind is DfoServer.Game.Raids.RaidKind.Anton or DfoServer.Game.Raids.RaidKind.Luke or DfoServer.Game.Raids.RaidKind.Prey)
                    await context.Notify(NotiPacketType.EPLP_RECHALLENGE, DungeonSettlementPackets.RetryEligibility(false),
                        "raid_entry_retry_disabled", Current);
                await PublishPartyRosterAsync(context, notice.Members, Current);
                await PublishPartyHostAsync(context, notice.Members, Current);
                await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(scene.Entry), "party_dungeon_information", Current);
                if (OrdinaryLukeProgress.Applies(scene.Entry)) await OrdinaryLukeSequenceProjection.RefreshAsync(context, Current);
                if (PreyNormalProgress.Applies(scene.Entry)) await PreyNormalSequenceProjection.RefreshAsync(context, Current);
                if (AntonNormalProgress.Applies(scene.Entry)) await AntonNormalSequenceProjection.RefreshAsync(context, Current);
                await MonsterAiProjection.SendAsync(context, scene, "party_monster_ai", Current);
                await context.Notify(NotiPacketType.START_MAP,
                    DungeonPackets.StartMap(scene.Entry, scene.Monsters, scene.MapSeed, passiveItems: scene.PassiveItems), "party_start_map", Current);
                if (DungeonPackets.HellPartyMonsterInfo(scene.Monsters) is { } entryHell)
                    await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO,entryHell,"party_hell_monsters",Current);
                if (!scene.HellConsumedSlots.IsEmpty)
                {
                    log.Write("hell_party_entry_ticket_consumed",new { Connection = context.Id, Party = true, scene.Entry.DungeonId,
                        scene.HellTicket, scene.LegendTicket, Slots = scene.HellConsumedSlots.Select(slot => slot.Index).ToArray() });
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(scene.Character.Inventory,
                        InventoryListType.Main,[.. scene.HellConsumedSlots]),"party_hell_entry_items",Current);
                }
                if (!scene.EntryItemConsumedSlots.IsEmpty)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(scene.Character.Inventory,
                        InventoryListType.Main, [.. scene.EntryItemConsumedSlots]), "party_entry_items", Current);
                if (!scene.ChannelConsumedSlots.IsEmpty)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(scene.Character.Inventory,
                        InventoryListType.Main, [.. scene.ChannelConsumedSlots]), "party_channel_entry_items", Current);
                var currencySlots = scene.EntryChangedCurrencies.Select(DfoServer.Game.Inventory.Services.ItemSlotBoundService.CurrencySlot).OfType<short>()
                    .Select(slot => new InventorySlot(InventoryListType.Main, slot)).ToArray();
                if (currencySlots.Length != 0)
                    await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(scene.Character.Inventory,
                        InventoryListType.Main, currencySlots), "party_entry_currencies", Current);
                if (scene.EntryChangedCurrencies.Any(kind => kind is >= CurrencyKind.Cera and <= CurrencyKind.HappyTokenCera))
                    await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(scene.Character.Inventory.AccountWallet), "party_entry_cera", Current);
                foreach (var count in scene.EnterCounts)
                    await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Info(count.Dungeon, count.Daily, count.Weekly), "party_entry_count", Current);
                if (scene.Entry.Dimension is not null) await DimensionEntranceProjection.RefreshAsync(context, Current, entranceOnly: false);
                RaidCombatProjection.AntonTimersForScene(context, Current);
                break;
            case PartyDungeonNoticeKind.Loaded:
                if (scene.NavigationReplay) break;
                await context.Reply(CmdPacketType.FINISH_LOADING, [1], "party_loading_ack", Current);
                await context.Notify(NotiPacketType.FINISH_LOADING, DungeonPackets.LoadingNotice(), "party_loading_release", Current);
                foreach (var member in notice.Members)
                    await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(member.Actor.Character.Id), "party_player_state", Current);
                // 组队副本加载确认同样重建Actor，按当前网吧身份补发特权buff。
                await PcRoomBuffProjection.PostAsync(context, Current);
                town.GuildEconomy?.Post(context, force: true);
                await GuildTrainingProjection.PostAsync(context);
                await BossSelectionBuffProjection.PostAsync(context, force: true, canSend: Current);
                GuildDungeonPartyBuffProjection.Post(context);
                if (scene.ScriptedReturnId != Guid.Empty)
                {
                    if (published) context.Session.ConfirmScriptedReturnLoading(scene);
                    break;
                }
                if (scene.Entry.UsesFatigue)
                    await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(scene.Character)), "party_room_fatigue", Current);
                if (context.Elevator is not null) await context.Elevator.StartAsync(scene);
                if (scene.HellPartyRoomCleared)
                    await context.Notify(NotiPacketType.HELL_PARTY_CLEAR,DungeonPackets.HellPartyCleared(),"party_hell_clear",Current);
                if (scene.Snapshot.Phase == SinglePlayerDungeonPhase.Settlement)
                    await DungeonSettlementProjection.EnableAsync(context);
                await PublishEpicPieceShopAsync(context, prepare: false);
                // 首次场景安装在30之后才完整可用；重放真实参与表和HP，不沿用加载前已写帧的去重缓存。
                RaidCombatProjection.RestoreStatusForScene(context, Current);
                break;
            case PartyDungeonNoticeKind.Move:
                if (scene.NavigationReplay) break;
                await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "party_move_ack", Current);
                await MonsterAiProjection.SendAsync(context, scene, "party_monster_ai", Current);
                await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(scene.Entry, scene.Monsters, scene.MapSeed,
                    scene.Snapshot.MapOperation, scene.Snapshot.MapPayload, scene.PassiveItems), "party_next_map", Current);
                if (DungeonPackets.HellPartyMonsterInfo(scene.Monsters) is { } moveHell && scene.Snapshot.MapPayload != DungeonMapPayload.Cached)
                    await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO,moveHell,"party_hell_monsters",Current);
                if (published && scene.ScriptedReturnId != Guid.Empty) context.Session.ConfirmScriptedReturnPresentation(scene);
                break;
        }
    }

    async Task<CommandResult> PartyDeathAsync(GameConnectionContext context, Packet request)
    {
        var instance = context.Session.PartyDungeon!;
        if (DungeonRequests.TryParsePassiveDeath(request.Body,out var passive))
        {
            using var peerGates = await PartyDungeonProjection.AcquirePeersAsync(context,instance.Members);
            string fingerprint = Convert.ToHexString(SHA256.HashData(request.Body));
            var notices = instance.ObservePassive(context.Session,passive!.ObjectIdentifier,fingerprint,
                session => session.ObserveFormalPassiveDeath(passive.ObjectIdentifier,passive.ChecksumMatches,fingerprint,rooms));
            log.Write("formal_passive_death",new { Connection = context.Id, Party = instance.Id, Accepted = notices.Length > 0, Request = passive,
                EnergyRejection = notices.Length == 0 && context.Session.IsLukeEnergyDungeon
                    ? context.Session.DiagnosePassiveEvent(passive.ObjectIdentifier, rooms) : null });
            await PartyDungeonProjection.PublishAsync(notices);
            return CommandResult.Handled;
        }
        Func<DfoServer.Game.Session.GameSession, FormalDungeonDeathResult> observe;
        uint key;
        if (DungeonRequests.TryParseScriptedDeath(request.Body, out var scripted))
        {
            key = scripted!.ObjectKey;
            var observation = new NativeDungeonScriptedDeathObservation(key, scripted.RewriteFlag, scripted.ChecksumMatches,
                Convert.ToHexString(SHA256.HashData(request.Body)));
            observe = session => session.ObserveFormalDungeonScriptedDeath(observation, rewards, growth);
        }
        else
        {
            if (!DungeonRequests.TryParseDeath(request.Body, out var parsed))
                return CommandResult.Unhandled;
            if (parsed!.SinglePlayerStatistics is not { } statistics)
                return await PublishDeathAsync(context,new(DungeonRunOutcome.Rejected),context.Session.Selected,parsed.ObjectKey,parsed);
            key = parsed.ObjectKey;
            var part = statistics.Participant;
            var observation = new NativeDungeonDeathObservation(key, parsed.Reporter, part.Kind, part.ObjectKey,
                part.CumulativeHpDecrease, part.PositiveHpDecreaseCount, statistics.RewriteFlag, statistics.SpawnLevel,
                statistics.SpawnHpSnapshot, statistics.Unknown18, statistics.Unknown26, statistics.ChecksumMatches,
                Convert.ToHexString(SHA256.HashData(request.Body)))
            { Participants = statistics.Participants.Select(p => new NativeDungeonDeathParticipant(p.Kind, p.ObjectKey,
                p.CumulativeHpDecrease, p.PositiveHpDecreaseCount)).ToImmutableArray() };
            observe = session => session.ObserveFormalDungeonDeath(observation, rewards, growth);
        }
        if (key > ushort.MaxValue)
            return await PublishDeathAsync(context,new(DungeonRunOutcome.Rejected),context.Session.Selected,key,new { key });
        using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        var results = instance.ObserveDeath(context.Session, (ushort)key, observe);
        await PartyDungeonProjection.PublishAsync(results);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PartyPickupAsync(GameConnectionContext context, uint key)
    {
        var instance = context.Session.PartyDungeon!;
        using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members);
        var results = instance.Pickup(context.Session, key,
            session => session.PickFormalDungeonItem(key, items!, pieces!, goldLimits));
        if (results.Length == 0)
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Rejected(19), "party_pickup_rejected");
        else await PartyDungeonProjection.PublishAsync(results);
        return CommandResult.Handled;
    }

    async Task PublishPartyPickupAsync(GameConnectionContext context, PartyDungeonNotice notice)
    {
        var result = notice.Pickup!;
        if (result.Receipt is not { } picked || result.Commit is not { } commit || notice.Scene is not { } scene)
        {
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Rejected(result.CapacityRejected ? (ushort)22 : (ushort)19),
                "party_pickup_rejected");
            return;
        }
        bool Guard() => context.Session.CanPublishFormalDungeon(scene);
        uint key = result.Ground!.Reservation.GroundKey;
        var destination = commit.Currency is { } currency
            ? new InventorySlot(InventoryListType.Main, CurrencyPackets.PickupSlot(currency)) : commit.Destination;
        var body = commit.Currency == CurrencyKind.Gold
            ? DungeonPickupPackets.IndependentGoldNotification(key, picked.Character.Id, checked((uint)commit.Amount), notice.Recipient.Slot)
            : DungeonPickupPackets.OrdinaryNotification(key, picked.Character.Id, new byte[8], picked.Character.Id,
                commit.EpicPiece is not null || commit.AccountMaterial is not null
                    ? DungeonPickupPackets.NoInventorySlot : checked((ushort)destination.Index), 0);
        await context.Notify(NotiPacketType.GET_ITEM, body, "party_pickup_notice", Guard);
        if (scene.Character.Id != picked.Character.Id) return;
        if (!commit.ProjectionCurrent) { context.Disconnect(); return; }
        if (commit.EpicPiece is { } fragment)
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO,
                EpicBookPackets.Info(picked.Character.Inventory.EpicPieces, pieces!, [fragment.Index]), "party_pickup_epic_piece", Guard);
        else if (commit.AccountMaterial is not null)
            await town.AccountMaterialsAsync(context, picked.Character, canSend: Guard);
        else
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(picked.Character.Inventory,
                destination.List, [destination]), "party_pickup_inventory", Guard);
        if (scene.Character.Id == notice.Peer)
            await context.Reply(CmdPacketType.GET_ITEM, DungeonPickupPackets.Accepted(), "party_pickup_ack", Guard);
        if (picked.Character.ActiveMainQuests.Length > 0)
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(picked.Character.ActiveMainQuests), "party_pickup_quests", Guard);
    }
}
