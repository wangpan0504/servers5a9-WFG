using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
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
    GameWorld.Adventure.AdventureCatalog? adventure = null) : IGameCommandModule
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
        await DungeonSettlementProjection.EnableAsync(context, parsed.BossKey);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ResultAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonSettlementRequests.TryParsePlayResult(request.Body, out var parsed)) return CommandResult.Unhandled;
        // 活动查询不占用账号锁；同一份结算准备冻结资格与倍率。
        var goldCardEvent = events?.Read(Game.Events.ServerEventService.DoubleGoldCardId);
        var result = context.Session.SettleFormalDungeon(parsed!.ActorId, parsed.RankPoint, parsed.MaximumRankPoint,
            request.Body, dungeons, rewards, growth, cards, rental?.LuckyStarStackLimit ?? 0, goldCardEvent, adventure);
        log.Write("formal_dungeon_settlement", new { Connection = context.Id, result.Outcome, parsed.RankPoint,
            result.Reward, result.GainedExperience, result.Card, result.MailId, result.Receipt?.Snapshot.Identity,
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
        uint plainClear = reward.TotalExperience - reward.GrowthContractExperience - reward.PotionExperience - reward.AdventureExperience;
        var display = ExperienceBonuses.FromClearReward(reward)?.Display(result.GainedExperience) ?? CharacterExperienceDisplay.Ordinary with
        { GrowthContractExperience = GrowthContractPolicy.AppliedBonus(plainClear, reward.GrowthContractExperience, result.GainedExperience),
            PotionExperience = ExpBonusPolicy.Shown(plainClear, reward.GrowthContractExperience, reward.PotionExperience, result.GainedExperience) };
        bool Guard() => context.Session.CanPublishDungeonSettlement(receipt);
        // 大会副本没有普通结算页：不发 34/35，改发 NOTI388（见 TournamentReward 注释）。
        bool tournament = receipt.Entry.IsTournament;
        if (!tournament)
            await context.Notify(NotiPacketType.PLAY_RESULT, DungeonSettlementPackets.PlayResult(receipt.Character.Id, result.Presentation!), "formal_play_result", Guard);
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
        if (!receipt.Entry.IsTutorial)
            await DungeonCardMembershipProjection.RefreshAsync(context);
        if (tournament)
        {
            context.TournamentRewardSelection[0] = context.TournamentRewardSelection[1] = DungeonSettlementPackets.TournamentUnselected;
            await context.Notify(NotiPacketType.TOURNAMENT_CLEAR_REWARD,
                DungeonSettlementPackets.TournamentReward(DungeonTournamentPlan.RoundCount, result.GainedExperience), "formal_tournament_clear_reward", Guard);
        }
        else
        await context.Notify(NotiPacketType.CLEAR_DUNGEON_REWARD, ExclusiveDropPackets.ClearCard(reward.Applied(result.GainedExperience),
            result.Presentation!.MonsterExperience, result.Card, checked((int)receipt.Entry.DungeonId), result.Options, result.BlackDiamond,
            result.Presentation.MonsterGrowthContractExperience), "formal_clear_reward", Guard);
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
        await context.Reply(CmdPacketType.CARD_SELECT_RIGHT_STATE, DungeonSettlementPackets.CardRights(checked((short)fatigue.Remaining)), "formal_card_rights", () => context.Session.CanPublishDungeonSettlement(receipt));
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
            if (retry is null) return CommandResult.Unhandled;
            bool RetryGuard() => context.Session.CanPublishDungeonSettlement(retry);
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_retry", RetryGuard);
            // 261只更新结算页的再挑战资格，不能结束旧Scene；27沿已核验入口切换场景并清理旧结算层。
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(), "formal_retry_scene_transition", RetryGuard);
            await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(retry.Entry), "formal_retry_information", RetryGuard);
            await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(retry.Entry, retry.Monsters, retry.MapSeed,
                passiveItems: retry.PassiveItems), "formal_retry_start_map", RetryGuard);
            return CommandResult.Handled;
        }
        if (option == 1)
        {
            // 强袭没有主线后继。续图失败且原因不是 no-accepted-successor 时不回 ACK，结算页会停住。
            if (!context.Session.IsFormalDungeonAssault())
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
            var opening = context.Session.BeginFormalDungeonOtherSelection();
            if (opening is null) return CommandResult.Unhandled;
            bool OpeningGuard() => context.Session.CanPublishDungeonSettlement(opening);
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_other_selection", OpeningGuard);
            // 先唤醒结算页，由后续真实15建立完整选图，避免提前发布27。
            await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "formal_result_selector_wake", OpeningGuard);
            return CommandResult.Handled;
        }
        if (option == 3)
        {
            var opening = context.Session.ConfirmFormalDungeonOtherSelection();
            if (opening is null) return CommandResult.Unhandled;
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_selector_confirm", () => context.Session.CanPublishTravel(opening));
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
