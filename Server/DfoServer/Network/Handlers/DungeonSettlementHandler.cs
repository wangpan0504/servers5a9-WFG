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
    RentalCatalog? rental = null, Game.Events.ServerEventService? events = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BOSS_DIE_CHECK, BossAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SET_PLAY_RESULT, ResultAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SCORE_SCROLL_STATE, ScrollAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.CARD_SELECT_RIGHT_STATE, CardsAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.SELECT_CARD, SelectCardAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
        commands.Register(CmdPacketType.EPLP_COMMAND, ResultCommandAsync, nameof(DungeonSettlementHandler), UserStateRequirement.DungeonContext);
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
            request.Body, dungeons, rewards, growth, cards, rental?.LuckyStarStackLimit ?? 0, goldCardEvent);
        log.Write("formal_dungeon_settlement", new { Connection = context.Id, result.Outcome, parsed.RankPoint,
            result.Reward, result.GainedExperience, result.Card, result.MailId, result.Receipt?.Snapshot.Identity });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) || result.Receipt is not { } receipt)
            return CommandResult.Unhandled;
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("结算角色缺成长附属owner");
        var reward = result.Reward ?? throw new InvalidOperationException("已提交结算缺少经验奖励");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints),
            state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience);
        var display = CharacterExperienceDisplay.Ordinary;
        bool Guard() => context.Session.CanPublishDungeonSettlement(receipt);
        await context.Notify(NotiPacketType.PLAY_RESULT, DungeonSettlementPackets.PlayResult(receipt.Character.Id, result.Presentation!), "formal_play_result", Guard);
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(receipt.Character.Level, receipt.Character.Learning, auxiliary, display), "formal_clear_experience", Guard);
        var previous = result.PreviousCharacter ?? throw new InvalidOperationException("通关结算缺少真实提交前角色");
        if (!previous.ActiveMainQuests.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "formal_clear_quest_conditions",Guard);
        if (SkillPackets.BindingsChanged(previous.Learning, receipt.Character.Learning))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(receipt.Character.Learning), "formal_clear_skills", Guard);
        await levelProjection.RefreshAsync(context, receipt.Character, previous.Level, "formal_clear_level_committed", Guard);
        if (!receipt.Entry.IsTutorial)
            await DungeonCardMembershipProjection.RefreshAsync(context);
        await context.Notify(NotiPacketType.CLEAR_DUNGEON_REWARD, DungeonSettlementPackets.CardReward(reward.Applied(result.GainedExperience), result.Presentation!.MonsterExperience, result.Card,result.Options,result.BlackDiamond), "formal_clear_reward", Guard);
        if (result.LuckyStarGained)
            await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST,
                RentalPackets.State(receipt.Character.Inventory, DateTimeOffset.UtcNow),
                "formal_clear_lucky_star", Guard);
        if (result.Inventory is { } inventory)
            await (inventoryProjection ?? throw new InvalidOperationException("免费牌缺少库存投影")).RefreshAsync(context, inventory);
        if (result.MailId is not null) context.Session.NotifyMailbox(receipt.Character.Id);
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
            var retry = context.Session.RetryFormalDungeon(current, dungeons, rooms, drops, items,pieces);
            if (retry is null) return CommandResult.Unhandled;
            bool RetryGuard() => context.Session.CanPublishDungeonSettlement(retry);
            await context.Reply(CmdPacketType.EPLP_COMMAND, DungeonSettlementPackets.ResultCommand(state, option), "formal_result_retry", RetryGuard);
            // 261只更新结算页的再挑战资格，不能结束旧Scene；27沿已核验入口切换场景并清理旧结算层。
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(), "formal_retry_scene_transition", RetryGuard);
            await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(retry.Entry), "formal_retry_information", RetryGuard);
            await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(retry.Entry, retry.Monsters, retry.MapSeed), "formal_retry_start_map", RetryGuard);
            return CommandResult.Handled;
        }
        if (option == 1)
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
