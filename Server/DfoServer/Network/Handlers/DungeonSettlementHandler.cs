using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class DungeonSettlementHandler(ServerLog log, DungeonCatalog dungeons,
    DungeonClearExperienceCatalog rewards, CharacterGrowthCatalog growth, TownProjection town,
    DungeonRoomCatalog rooms, DungeonMonsterDropCatalog drops, ItemCatalog items,
    CharacterLevelProjection levelProjection, EpicPieceCatalog? pieces = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BOSS_DIE_CHECK, BossAsync, nameof(DungeonSettlementHandler));
        commands.Register(CmdPacketType.SET_PLAY_RESULT, ResultAsync, nameof(DungeonSettlementHandler));
        commands.Register(CmdPacketType.SCORE_SCROLL_STATE, ScrollAsync, nameof(DungeonSettlementHandler));
        commands.Register(CmdPacketType.CARD_SELECT_RIGHT_STATE, CardsAsync, nameof(DungeonSettlementHandler));
        commands.Register(CmdPacketType.SELECT_CARD, SelectCardAsync, nameof(DungeonSettlementHandler));
        commands.Register(CmdPacketType.EPLP_COMMAND, ResultCommandAsync, nameof(DungeonSettlementHandler));
    }

    async Task<CommandResult> BossAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonSettlementRequests.TryParseBossCheck(request.Body, out var parsed)) return CommandResult.Unhandled;
        var receipt = context.Session.ConfirmFormalDungeonBoss(parsed!.ActorId, parsed.BossKey, parsed.SpawnLevel, parsed.SpawnHp);
        if (receipt is null) return CommandResult.Unhandled;
        if (context.Session.EnableFormalDungeonSettlement(parsed.BossKey) is { } completion)
        {
            bool Guard() => context.Session.CanPublishDungeonSettlement(completion.Receipt);
            await context.Notify(NotiPacketType.BOSS_DIE_CHECK, DungeonSettlementPackets.BossAccepted(completion.BossKey), "formal_boss_confirmed", Guard);
            bool enabled = false;
            await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "formal_clear_enabled", () => enabled = Guard());
            if (enabled) context.Session.ConfirmFormalDungeonSettlementEnabled(completion.Receipt);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> ResultAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonSettlementRequests.TryParsePlayResult(request.Body, out var parsed)) return CommandResult.Unhandled;
        var result = context.Session.SettleFormalDungeon(parsed!.ActorId, parsed.RankPoint, parsed.MaximumRankPoint,
            request.Body, dungeons, rewards, growth);
        log.Write("formal_dungeon_settlement", new { Connection = context.Id, result.Outcome, parsed.RankPoint,
            result.Reward, result.Receipt?.Snapshot.Identity });
        if (result.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate) || result.Receipt is not { } receipt)
            return CommandResult.Unhandled;
        var state = receipt.Character.DungeonState ?? throw new InvalidOperationException("结算角色缺成长附属owner");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)receipt.Character.Inventory.WinPoints),
            state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience);
        var display = new CharacterExperienceDisplay(result.Reward!.TotalExperience, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0);
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
        await context.Notify(NotiPacketType.CLEAR_DUNGEON_REWARD, DungeonSettlementPackets.EmptyCardReward(result.Reward, result.Presentation!.MonsterExperience), "formal_clear_reward", Guard);
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
        await context.Reply(CmdPacketType.CARD_SELECT_RIGHT_STATE, DungeonSettlementPackets.CardRights(), "formal_empty_card_rights", () => context.Session.CanPublishDungeonSettlement(receipt));
        return CommandResult.Handled;
    }

    async Task<CommandResult> SelectCardAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 2 || context.Session.SelectFormalDungeonEmptyCard(request.Body[0], request.Body[1]) is not { } receipt)
            return CommandResult.Unhandled;
        await context.Reply(CmdPacketType.SELECT_CARD, DungeonSettlementPackets.EmptyCardSelected(), "formal_empty_card_selected", () => context.Session.CanPublishDungeonSettlement(receipt));
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
            await context.Notify(NotiPacketType.EPLP_RECHALLENGE, [9], "formal_retry_ready", RetryGuard);
            await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(retry.Entry), "formal_retry_information", RetryGuard);
            await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(retry.Entry, retry.Monsters, retry.MapSeed), "formal_retry_start_map", RetryGuard);
            return CommandResult.Handled;
        }
        if (option == 1)
        {
            var continuation = context.Session.BeginFormalDungeonQuestContinuation(current,dungeons,rooms,drops,items,pieces,out string reason);
            if (continuation is not null)
            {
                log.Write("formal_quest_continuation",new { Connection = context.Id, Accepted = true, Reason = reason, current.Entry.RouteQuestId });
                if (context.Session.LeaveFormalDungeonForContinuation() is not { } returning) return CommandResult.Handled;
                await context.Reply(CmdPacketType.EPLP_COMMAND,DungeonSettlementPackets.ResultCommand(state,option),
                    "formal_result_next_quest",() => context.Session.CanPublishTravel(returning));
                await town.ReturnFromFormalDungeonAsync(context,returning,"formal_next_quest_town_boundary");
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
        return CommandResult.Handled;
    }
}
