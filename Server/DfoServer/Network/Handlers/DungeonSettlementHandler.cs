using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class DungeonSettlementHandler(ServerLog log, DungeonCatalog dungeons,
    DungeonClearExperienceCatalog rewards, CharacterGrowthCatalog growth, TownProjection town,
    DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items,
    CharacterLevelProjection levelProjection, EpicPieceCatalog? pieces = null,
    DungeonCardRewardPolicy? cards = null, InventoryProjection? inventoryProjection = null,
    RentalCatalog? rental = null, Game.Events.ServerEventService? events = null,
    GameWorld.Adventure.AdventureCatalog? adventure = null, PartyDungeonService? partyDungeons = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BOSS_DIE_CHECK, BossAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SET_PLAY_RESULT, ResultAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SCORE_SCROLL_STATE, ScrollAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.CARD_SELECT_RIGHT_STATE, CardsAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SELECT_CARD, SelectCardAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.EPLP_COMMAND, ResultCommandAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE, TournamentStateAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.TOURNAMENT_REWARD_SELECT, TournamentSelectAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
    }

    async Task<CommandResult> BossAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonSettlementRequests.TryParseBossCheck(request.Body, out var parsed)) return CommandResult.Unhandled;
        var receipt = context.Session.ConfirmFormalDungeonBoss(parsed!.ActorId, parsed.BossKey, parsed.SpawnLevel, parsed.SpawnHp)
            ?? context.Session.ConfirmTutorialCinematic(parsed.ActorId, parsed.BossKey);
        if (receipt is null) return CommandResult.Unhandled;
        if (context.Session.RaidCombatView() is not null)
        {
            bool phaseProgress = context.Session.RaidPhaseProgress;
            if (context.Session.CompleteRaidCombatDungeon(receipt) is { } change)
            {
                context.Session.PublishRaidCombat();
                if (phaseProgress)
                {
                    int phaseSuccess = change.After.Symbols.TryGetValue("[1phase success]", out int success) ? success : -1;
                    log.Write("anton_phase_progress", new { Connection = context.Id, change.After.Phase,
                        State = change.After.State.ToString(), PhaseSuccess = phaseSuccess, Timers = change.After.Timers.Count,
                        Deferred = change.After.Deferred.Count, TimerUnit = "unknown" });
                }
            }
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(parsed.BossKey), "raid_boss_confirmed");
            return CommandResult.Handled;
        }
        await DungeonSettlementProjection.EnableAsync(context, parsed.BossKey);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ResultAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonSettlementRequests.TryParsePlayResult(request.Body, out var parsed)) return CommandResult.Unhandled;
        var participant = parsed!.Participants.SingleOrDefault(member => member.ActorId == context.Session.Selected?.Id);
        if (participant is null) return CommandResult.Unhandled;
        // 活动查询不占用账号锁；同一份结算准备冻结资格与倍率。
        var goldCardEvent = events?.Read(Game.Events.ServerEventService.DoubleGoldCardId);
        var result = context.Session.SettleFormalDungeon(participant.ActorId, participant.RankPoint, parsed.MaximumRankPoint,
            request.Body, dungeons, rewards, growth, cards, rental?.LuckyStarStackLimit ?? 0, goldCardEvent, adventure,items,pieces,
            parsed.Participants.Select(member => member.ActorId).ToArray());
        log.Write("formal_dungeon_settlement", new { Connection = context.Id, result.Outcome, participant.RankPoint,
            parsed.MaximumRankPoint, Members = parsed.Participants.Count,
            result.Reward, result.GainedExperience, result.ProficiencyGained, result.Card, result.GriefReward, result.MailId, result.Receipt?.Snapshot.Identity,
            Adventure = result.Adventure is { } progression
                ? new { progression.State.Level, progression.State.Experience, progression.MaximumLevelExperience,
                    progression.State.CapsuleExperience, BravePoints = progression.State.Points[0] }
                : null });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) || result.Receipt is not { } receipt)
            return CommandResult.Unhandled;
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("结算角色缺成长附属owner");
        var reward = result.Reward ?? throw new InvalidOperationException("已提交结算缺少经验奖励");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints),
            state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience);
        // 本版协议没有独立的冒险团加成字段：它属于基础通关经验，和基础项一起作为普通经验展示。
        // 满级入口线后角色经验到账为0，结算面板、大会奖励与掉落卡都按「角色到账 + 熟练度到账」的有效经验展示。
        uint effective = checked(result.GainedExperience + result.ProficiencyGained);
        uint plainClear = reward.TotalExperience - reward.GrowthContractExperience - reward.PotionExperience - reward.AdventureExperience;
        var display = ExperienceBonuses.FromClearReward(reward)?.Display(effective) ?? CharacterExperienceDisplay.Ordinary with
        { GrowthContractExperience = GrowthContractPolicy.AppliedBonus(plainClear, reward.GrowthContractExperience, effective),
            PotionExperience = ExpBonusPolicy.Shown(plainClear, reward.GrowthContractExperience, reward.PotionExperience, effective) };
        bool Guard() => context.Session.CanPublishDungeonSettlement(receipt);
        // 大会副本没有普通结算页：不发 34/35，改发 NOTI388（见 TournamentReward 注释）。
        bool tournament = receipt.Entry.IsTournament;
        // 超星空保留真实通关经验提交，但不发布普通结果页或翻牌奖励。
        bool cosmo = receipt.Entry.IsCosmo;
        bool grief = receipt.Entry.Maze.IsGriefTower;
        if (!tournament && !cosmo && !grief)
            await context.Notify(NotiPacketType.PLAY_RESULT, DungeonSettlementPackets.PlayResult(receipt.Character.Id, result.Presentation!, receipt.PartyActors), "formal_play_result", Guard);
        var previous = result.PreviousCharacter ?? throw new InvalidOperationException("通关结算缺少真实提交前角色");
        if (CharacterExperienceNotice.ShouldPublish(previous.Level))
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(receipt.Character.Level, receipt.Character.Learning, auxiliary, display), "formal_clear_experience", Guard);
        if (!previous.ActiveMainQuests.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "formal_clear_quest_conditions",Guard);
        if (previous.Level == receipt.Character.Level && SkillPackets.BindingsChanged(previous.Learning, receipt.Character.Learning))
            await SkillProjection.PublishAsync(context, receipt.Character, "formal_clear_skills", "formal_clear_combo", Guard);
        if (result.Inventory is null)
            await SkillSwitchInventoryProjection.RefreshStateAsync(
                context,
                previous.SkillSwitchInventory,
                previous.Inventory,
                receipt.Character,
                Guard,
                "formal_clear_skill_switch_inventory");
        await levelProjection.RefreshAsync(context, receipt.Character, previous.Level, "formal_clear_level_committed", Guard,
            previousLearning: previous.Learning);
        if (!receipt.Entry.IsTutorial && !cosmo && !grief)
            await DungeonCardMembershipProjection.RefreshAsync(context);
        if (tournament)
        {
            context.TournamentRewardSelection[0] = context.TournamentRewardSelection[1] = DungeonSettlementPackets.TournamentUnselected;
            await context.Notify(NotiPacketType.TOURNAMENT_CLEAR_REWARD,
                DungeonSettlementPackets.TournamentReward(DungeonTournamentPlan.RoundCount, effective), "formal_tournament_clear_reward", Guard);
        }
        else if (grief)
            await context.Notify(NotiPacketType.TOG_CLEAR_REWARD,GriefTowerPackets.ClearReward(receipt.ClearTimeMilliseconds,
                result.Receipt.Entry.GriefLayer ?? throw new InvalidOperationException("悲叹通关缺少已冻结楼层"),
                result.GriefReward ?? throw new InvalidOperationException("悲叹通关缺少已提交奖励")),"formal_grief_clear_reward",Guard);
        else if (!cosmo)
        await context.Notify(NotiPacketType.CLEAR_DUNGEON_REWARD, ExclusiveDropPackets.ClearCard(reward.Applied(effective),
            result.Presentation!.MonsterExperience, result.Card, checked((int)receipt.Entry.DungeonId), result.Options, result.BlackDiamond,
            result.Presentation.MonsterGrowthContractExperience, result.Presentation.MonsterEquipmentExperience,
            result.Presentation.MonsterExperienceEntries, result.Presentation.MonsterBossExperience, receipt.PartySlot), "formal_clear_reward", Guard);
        if (dungeons.Get(checked((int)receipt.Entry.DungeonId)).IsDazzlementTower)
            await DazzlementTowerProjection.StateAsync(context, Guard);
        if (result.LuckyStarGained)
            await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST,
                RentalPackets.State(receipt.Character.Inventory, DateTimeOffset.UtcNow),
                "formal_clear_lucky_star", Guard);
        if (result.Inventory is { } inventory)
            await (inventoryProjection ?? throw new InvalidOperationException("免费牌缺少库存投影")).RefreshAsync(context, inventory);
        if (result.AssaultClear is { } assaultClear)
        {
            log.Write("assault_mode_clear_committed", new { Connection = context.Id, assaultClear.Counts.Clears, assaultClear.Counts.Hidden,
                RewardItem = assaultClear.Reward?.ItemId, RewardCount = assaultClear.Reward?.Count, assaultClear.RewardIssue });
            if (assaultClear.Inventory is { } assaultInventory)
                await (inventoryProjection ?? throw new InvalidOperationException("assault reward projection unavailable")).RefreshAsync(context, assaultInventory);
            await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO, AssaultModePackets.Today(TimeProvider.System, assaultClear.Counts),
                "assault_mode_daily_info", Guard);
        }
        if (result.CrackClear is { } crackClear)
        {
            log.Write("crack_dimension_clear_committed", new { Connection = context.Id, crackClear.State.Clears,
                crackClear.State.DailyGranted, Granted = crackClear.Granted.Count, crackClear.Issue });
            if (crackClear.Inventory is { } crackInventory)
                await (inventoryProjection ?? throw new InvalidOperationException("次元裂缝奖励缺少库存投影")).RefreshAsync(context, crackInventory);
        }
        if (result.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
        if (cosmo) await DungeonSettlementProjection.RefreshCosmoRetryAsync(context);
        return CommandResult.Handled;
    }

    async Task<CommandResult> TournamentStateAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0 || context.Session.FormalDungeonResultFocus() is not { Entry.IsTournament: true } current)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.TOURNAMENT_REWARD_SELECT_STATE, DungeonSettlementPackets.TournamentSelectState(0),
            "formal_tournament_reward_state", () => context.Session.CanPublishDungeonSettlement(current));
        return CommandResult.Handled;
    }

    async Task<CommandResult> TournamentSelectAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2 || request.Body[0] >= DungeonSettlementPackets.TournamentCardRows ||
            request.Body[1] >= DungeonSettlementPackets.TournamentCardsPerRow ||
            context.Session.FormalDungeonResultFocus() is not { Entry.IsTournament: true } current)
            return CommandResult.Unhandled;
        var selected = context.TournamentRewardSelection;
        // 每行只能选一张；重复选同一张按幂等回显，选另一张忽略。卡均为空卡，不发放物品。
        if (selected[request.Body[0]] == DungeonSettlementPackets.TournamentUnselected) selected[request.Body[0]] = request.Body[1];
        await context.Reply(CmdPacketType.TOURNAMENT_REWARD_SELECT, DungeonSettlementPackets.TournamentSelection(0, selected),
            "formal_tournament_reward_selected", () => context.Session.CanPublishDungeonSettlement(current));
        return CommandResult.Handled;
    }

    async Task<CommandResult> ScrollAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0 || context.Session.AdvanceFormalDungeonCards(false) is not { } receipt)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.SCORE_SCROLL_STATE, [1], "formal_score_scroll", () => context.Session.CanPublishDungeonSettlement(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> CardsAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0 || context.Session.AdvanceFormalDungeonCards(true) is not { } receipt)
            return CommandResult.Unhandled;
        var view = context.Session.FormalCardState(receipt) ?? throw new InvalidOperationException("翻牌展示失效");
        var fatigue = receipt.Character.Fatigue ?? throw new InvalidOperationException("翻牌角色缺少疲劳owner");
        await context.Reply(CmdPacketType.CARD_SELECT_RIGHT_STATE, DungeonSettlementPackets.CardRights(checked((short)fatigue.Remaining), receipt.PartySlot), "formal_card_rights", () => context.Session.CanPublishDungeonSettlement(receipt));
        context.Cards ??= new DungeonCardProjection(context, log, inventoryProjection);
        await context.Cards.StartAsync(receipt);
        return CommandResult.Handled;
    }

    async Task<CommandResult> SelectCardAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2)
            return CommandResult.Unhandled;
        FormalDungeonReceipt? receipt;
        FormalFreeCardResult? free = null;
        if (request.Body[0] == 0)
        {
            free = context.Session.SelectFormalDungeonFreeCard(0,request.Body[1]);
            receipt = free?.Receipt;
        }
        else if (cards is not null)
        {
            var selected = context.Session.SelectFormalDungeonPaidCard(request.Body[0],request.Body[1],cards);
            if (selected is null)
            {
                log.Write("formal_paid_card_rejected",new {Connection=context.Id,Row=request.Body[0],Member=request.Body[1]});
                await DungeonCardMembershipProjection.RefreshAsync(context);
                return CommandResult.Handled;
            }
            receipt = selected.Receipt;
            var paidView = context.Session.FormalCardState(receipt) ?? throw new InvalidOperationException("付费牌展示失效");
            await context.Reply(CmdPacketType.SELECT_CARD,DungeonSettlementPackets.CardSelected(paidView),"formal_paid_card_selected",
                () => context.Session.CanPublishCardView(receipt,paidView));
            await DungeonCardMembershipProjection.RefreshAsync(context);
            if (selected.Inventory is { } inventory)
                await (inventoryProjection ?? throw new InvalidOperationException("付费牌缺少库存投影")).RefreshAsync(context,inventory);
            if (selected.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
            return CommandResult.Handled;
        }
        else return CommandResult.Unhandled;
        if (receipt is null) return CommandResult.Unhandled;
        var view = context.Session.FormalCardState(receipt) ?? throw new InvalidOperationException("免费牌展示失效");
        await context.Reply(CmdPacketType.SELECT_CARD, DungeonSettlementPackets.CardSelected(view), "formal_free_card_selected",
            () => context.Session.CanPublishCardView(receipt,view));
        if (free?.Inventory is { } freeInventory)
            await (inventoryProjection ?? throw new InvalidOperationException("免费牌缺少库存投影")).RefreshAsync(context, freeInventory);
        if (free?.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
        log.Write("formal_free_card_claimed", new { Connection = context.Id, receipt.Snapshot.Identity,
            free?.Duplicate, free?.MailId, Revision = receipt.Character.Revision });
        return CommandResult.Handled;
    }

    async Task<CommandResult> ResultCommandAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2 || request.Body[0] is not (1 or 2) || request.Body[1] > 3)
            return CommandResult.Unhandled;
        byte state = request.Body[0], option = request.Body[1];
        if (state == 1 && option == 3 && context.Session.HasFormalDungeonContinuation())
        {
            log.Write("formal_quest_continuation_tail",new { Connection = context.Id, state, option });
            return CommandResult.Handled;
        }
        if (context.Session.FormalDungeonResultFocus() is not { } current)
            return CommandResult.Unhandled;
        if (state == 2)
        {
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_focus", () => context.Session.CanPublishDungeonSettlement(current));
            return CommandResult.Handled;
        }
        if (context.Session.PartyDungeon is { } party)
        {
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, party.Members);
            PartyDungeonNotice[] notices = option switch
            {
                0 when partyDungeons is not null => partyDungeons.Enter(context.Session, current.Entry.DungeonId,
                    current.Entry.Difficulty, dungeons, rooms, drops, items, pieces, null)
                    .Select(n => n with { State = 1 }).ToArray(),
                1 => party.ResultSelection(context.Session),
                2 => party.ResultReturn(context.Session),
                _ => []
            };
            if (option == 1 && notices.Length > 0)
            {
                int target = context.Session.PrepareLegendDungeonSelection(dungeons);
                notices = notices.Select(n => n with { LegendDungeon = target }).ToArray();
            }
            if (option == 0 && notices.Length == 0 && current.Entry.IsCosmo)
                await PartyDungeonProjection.PublishAsync(party.Members.Select(m =>
                    new PartyDungeonNotice(PartyDungeonNoticeKind.RetryEligibility,m,party.Members,m.Actor.Session.CurrentFormalDungeonScene())));
            log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "party-result-command", option,
                Accepted = notices.Length > 0, Party = party.Id });
            await PartyDungeonProjection.PublishAsync(notices);
            return CommandResult.Handled;
        }
        if (option == 0)
        {
            // 强袭不能原地重开：PrepareFormalDungeon 对 retry 直接拒绝（assault-retry-or-class-experience），
            // 无 ACK 会让结算页停住，后面的回城/选图点击也不再发出。按 1/1 无后继的同一做法，回 ACK option 2 并回城。
            if (context.Session.IsFormalDungeonAssault())
            {
                var assaultReturn = context.Session.ReturnFromFormalDungeon();
                if (assaultReturn is null)
                {
                    log.Write("formal_result_assault_retry_rejected", new { Connection = context.Id });
                    return CommandResult.Unhandled;
                }
                await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, 2), "formal_result_assault_retry_return", () => context.Session.CanPublishTravel(assaultReturn));
                await town.ReturnFromFormalDungeonAsync(context, assaultReturn, "formal_result_town");
                log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "assault-retry", assaultReturn.Character.Id, assaultReturn.Character.Position });
                return CommandResult.Handled;
            }
            var retry = context.Session.RetryFormalDungeon(current, dungeons, rooms, drops, items,pieces);
            if (retry is null)
            {
                if (!current.Entry.IsCosmo && !current.Entry.IsInfernoParty) return CommandResult.Unhandled;
                if (current.Entry.IsCosmo)
                {
                    log.Write("formal_cosmo_retry_rejected", new { Connection = context.Id, current.Snapshot.Identity,
                        Reason = context.Session.LastFormalDungeonRejection });
                    await DungeonSettlementProjection.RefreshCosmoRetryAsync(context);
                    return CommandResult.Handled;
                }
                // 时空裂缝：票或疲劳不够时不能像密门深渊那样不回包，否则结算页停在等ACK、后面的选图和回城也不再发送。
                // 按强袭同一做法回ACK option 2 并回城，结算事实已提交，不退还也不重扣。
                log.Write("formal_inferno_retry_rejected", new { Connection = context.Id, current.Snapshot.Identity,
                    Reason = context.Session.LastFormalDungeonRejection });
                var infernoReturn = context.Session.ReturnFromFormalDungeon();
                if (infernoReturn is null) return CommandResult.Unhandled;
                await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, 2), "formal_result_inferno_retry_return",
                    () => context.Session.CanPublishTravel(infernoReturn));
                await town.ReturnFromFormalDungeonAsync(context, infernoReturn, "formal_result_town");
                log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "inferno-retry", infernoReturn.Character.Id, infernoReturn.Character.Position });
                return CommandResult.Handled;
            }
            bool RetryGuard() => context.Session.CanPublishDungeonSettlement(retry);
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_retry", RetryGuard);
            // 261只更新结算页的再挑战资格，不能结束旧Scene；27沿已核验入口切换场景并清理旧结算层。
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0, despairLayer: context.Session.DespairSelectionLayer()), "formal_retry_scene_transition", RetryGuard);
            await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(retry.Entry, retry.Snapshot.BossPosition), "formal_retry_information", RetryGuard);
            await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(retry.Entry, retry.Monsters, retry.MapSeed,
                passiveItems: retry.PassiveItems), "formal_retry_start_map", RetryGuard);
            if (!retry.HellConsumedSlots.IsDefaultOrEmpty)
            {
                await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(retry.Character.Inventory,
                    InventoryListType.Main, [.. retry.HellConsumedSlots]), "formal_retry_hell_items", RetryGuard);
                log.Write("hell_party_entry_ticket_consumed", new { Connection = context.Id, retry.Entry.DungeonId,
                    retry.HellTicket, retry.LegendTicket, retry.EntryFatigueCharged, retry.Snapshot.Identity });
            }
            return CommandResult.Handled;
        }
        if (option == 1)
        {
            if (current.Entry.IsCosmo)
            {
                // 实机菜单只发送72{1,1}，不会续发15；在此完成原子退场和完整选图。
                var selection = context.Session.BeginClearedHellSelection(current.Entry.DungeonId);
                if (selection is null) return CommandResult.Unhandled;
                int target = context.Session.PrepareLegendDungeonSelection(dungeons);
                if (!await town.DetachForDungeonSelectionAsync(context, selection)) return CommandResult.Handled;
                bool SelectionGuard() => context.Session.CanPublishDungeonSelection(selection);
                await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option),
                    "formal_cosmo_other_selection", SelectionGuard);
                await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "formal_cosmo_selection_opened", SelectionGuard);
                await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(selection.Character.Id), "formal_cosmo_selection_state", SelectionGuard);
                await GriefTowerProjection.SelectionAsync(context,dungeons,target,"formal_cosmo_selection_notice",SelectionGuard);
                log.Write("formal_hell_selection", new { Connection = context.Id, selection.Character.Id, selection.SelectionId,
                    current.Entry.DungeonId, LegendDungeon = target });
                return CommandResult.Handled;
            }
            // 强袭没有主线后继。续图失败且原因不是 no-accepted-successor 时不回 ACK，结算页会停住。
            if (!context.Session.IsFormalDungeonAssault() && current.Entry.RouteQuestId > 0)
            {
                var continuation = context.Session.BeginFormalDungeonQuestContinuation(current,dungeons,rooms,drops,items,pieces,
                    out bool returnToTown,out string reason);
                if (continuation is not null)
                {
                    log.Write("formal_quest_continuation",new { Connection = context.Id, Accepted = true, Reason = reason, current.Entry.RouteQuestId });
                    var returning = returnToTown ? context.Session.ReturnFromFormalDungeon() : context.Session.LeaveFormalDungeonForContinuation();
                    if (returning is null) return CommandResult.Handled;
                    // 无后继必须确认回城选项，不能让客户端继续执行下一任务的15入场链。
                    await context.Reply(CmdPacketType.EPLP_COMMAND,DungeonSettlementPackets.ResultCommand(state,returnToTown ? (byte)2 : option),
                        "formal_result_next_quest",() => context.Session.CanPublishTravel(returning));
                    await town.ReturnFromFormalDungeonAsync(context,returning,"formal_next_quest_town_boundary");
                    log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "next-quest", returnToTown,
                        returning.Character.Id, returning.Character.Position });
                    return CommandResult.Handled;
                }
                if (reason != "no-accepted-successor")
                {
                    log.Write("formal_quest_continuation",new { Connection = context.Id, Accepted = false, Reason = reason, current.Entry.RouteQuestId });
                    return CommandResult.Handled;
                }
            }
            var opening = context.Session.OpenFormalDungeonOtherSelection();
            if (opening is null) return CommandResult.Unhandled;
            int legendTarget = context.Session.PrepareLegendDungeonSelection(dungeons);
            if (!await town.DetachForDungeonSelectionAsync(context, opening)) return CommandResult.Handled;
            bool OpeningGuard() => context.Session.CanPublishDungeonSelection(opening);
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_other_selection", OpeningGuard);
            // 普通地图实机同样不续发15；确认后发送完整选图，后续选中副本的15仍沿正常入口。
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "formal_result_selector_wake", OpeningGuard);
            await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(opening.Character.Id), "formal_result_selection_state", OpeningGuard);
            await GriefTowerProjection.SelectionAsync(context,dungeons,legendTarget,"formal_result_selection_notice",OpeningGuard);
            log.Write("formal_dungeon_other_selection", new { Connection = context.Id, current.Entry.DungeonId,
                opening.Character.Id, opening.SelectionId, LegendDungeon = legendTarget });
            return CommandResult.Handled;
        }
        if (option == 3)
        {
            if (current.Entry.IsCosmo)
            {
                // 未确认选项1前的尾包不能退场；选图完成后旧菜单包由阶段与实例守卫拒绝。
                await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option),
                    "formal_cosmo_selector_confirm", () => context.Session.CanPublishDungeonSettlement(current));
                return CommandResult.Handled;
            }
            // 选项1已完成退场，未确认前到达的旧尾包不能单独释放当前实例。
            return CommandResult.Handled;
        }
        var receipt = context.Session.ReturnFromFormalDungeon();
        if (receipt is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishTravel(receipt);
        await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_return", Guard);
        await town.ReturnFromFormalDungeonAsync(context, receipt, "formal_result_town");
        log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "settlement", receipt.Character.Id, receipt.Character.Position });
        return CommandResult.Handled;
    }
}
