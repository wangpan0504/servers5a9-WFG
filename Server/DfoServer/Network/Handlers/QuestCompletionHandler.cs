using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class QuestCompletionHandler(QuestCatalog quests, QuestRewardParameterCatalog parameters,
    ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth,
    ServerLog log, CharacterLevelProjection levelProjection, QuestProjection? projection = null, CreatureProjection? creatures = null,
    ExpertJobScriptCatalog? expertJobs = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.FINISH_QUEST, FinishAsync, nameof(QuestCompletionHandler));

    private async Task<CommandResult> FinishAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TryFinish(packet.Body, out var request) ||
            request is not { Reserved: ushort.MaxValue } ||
            context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        if (context.Session.IsOperationHopeQuest(request.QuestId))
            return await FinishHopeAsync(context,request);
        var previous = context.Owner.Execute(() => context.Session.Selected);
        var result = context.Session.FinishQuest(request.QuestId, request.RewardSelector, quests, parameters, items, goldLimits, pieces, growth, request.Multiplier, expertJobs);
        log.Write("quest_completion", new { Connection = context.Id, Quest = request.QuestId, result.Outcome,
            Accepted = result.Outcome == QuestFinishOutcome.Committed, Failure = result.Failure.ToString(), RejectReason = result.Reason,
            result.Receipt?.Commit.AcceptanceId, result.Receipt?.Commit.Clear?.InstanceId,
            result.Receipt?.Commit.ExperienceReward, result.Receipt?.Commit.GainedExperience, result.Receipt?.Commit.Duplicate,
            result.Receipt?.Commit.DirectGoldAmount, result.Receipt?.Commit.MailedGoldAmount, result.Receipt?.Commit.GoldMailId, result.Receipt?.Commit.ResourceMailId });
        if (result.Outcome != QuestFinishOutcome.Committed || result.Receipt is not { } receipt)
        {
            if (result.RejectionReceipt is not { } rejection)
                return CommandResult.Unhandled;
            // 4有本版满包专属文案；19为本服中立拒绝，使用已证静默收尾，不声称原服各原因的错误映射。
            ushort error = result.Outcome == QuestFinishOutcome.InsufficientSpace ? (ushort)4 : (ushort)19;
            await context.Reply(CmdPacketType.FINISH_QUEST, QuestPackets.Rejected(error), "quest_completion_rejected",
                () => context.Session.CanPublishQuestRefusal(rejection));
            return CommandResult.Handled;
        }
        if (!receipt.Commit.ProjectionCurrent)
            return CommandResult.CloseConnection;
        bool Guard() => context.Session.CanPublishQuestCompletion(receipt);
        var commit = receipt.Commit;
        var character = commit.Character;
        var rewards = commit.Rewards.Select(row => new QuestRewardItemProjection(row.Item, row.GrantedCount)).ToArray();
        byte[] acknowledgement = QuestPackets.Finish(request.QuestId, commit.ConditionType, commit.GainedExperience,
                commit.ConditionType == 44 || request.QuestId==2674 && commit.ConditionType==60 && commit.TpAmount==2 &&
                    commit.Consumed is [{ItemId:10158452,Count:1}] ? [] : commit.Consumed.Select(row => new QuestConsumedItemProjection(row.Position.Index, row.Count)).ToArray(),
                commit.DirectGoldAmount, rewards,
                commit.ProfessionGrowType,commit.Currencies.Where(row => !QuestCurrencyReward.HasBalanceNotice(row.Kind)).Select(row => new QuestCurrencyRewardProjection(
                    ItemSlotBoundService.CurrencySlot(row.Kind) ?? throw new InvalidOperationException("钱包奖励缺少已确认持有域"),
                    row.Kind == DfoServer.Game.Currency.CurrencyKind.ReviveCoin ? 1 : row.ItemId,row.Count)).ToArray(),commit.TpAmount,commit.HellChallenge,
                creatureEvolution: QuestCreatureReward.Evolution(quests.Get(commit.QuestId)), expertJobType: commit.ExpertJobType);
        // 转职/觉醒先装载已提交技能快照，ACK34会据此重建本地技能；沿S4A21的前置通知顺序。
        bool profession = commit.ProfessionGrowType is not null;
        bool expertJob = commit.ExpertJobType is not null;
        if (expertJob)
            await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(character, context.Session.ReadPvpGrade(character), context.Channel,
                adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
                "quest_expert_job_actor", Guard);
        if (profession)
            await SkillProjection.PublishAsync(context, character, "quest_profession_skills", "quest_profession_combo", Guard);
        else if (expertJob)
            await SkillProjection.PublishAsync(context, character, "quest_expert_job_skills", "quest_expert_job_combo", Guard);
        // ACK34按itemId选择Pet owner，并以真实UID创建宠物；先安装提交后的详情表，不将宠物伪装Main物品。
        if (commit.Rewards.Any(row => row.Item.Creature is not null))
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(character.Inventory),
                "quest_reward_creature_details", Guard);
        await context.Reply(CmdPacketType.FINISH_QUEST, acknowledgement, "quest_completion_committed", Guard);
        if (creatures is not null && previous is not null && commit.ChangedSlots.Contains(Game.Creatures.CreatureRules.EquippedSlot))
            await creatures.ChangedAsync(context, previous.Inventory, character, receipt.Phase, Guard);
        // ACK34先消费真实增量，再投影完整Core及绝对成长；不在最终库存之后重放增量奖励。
        var inventorySlots = commit.ChangedSlots.Concat(commit.DirectGoldAmount == 0 ? [] :
            new[] { new InventorySlot(InventoryListType.Main, 0) }).ToArray();
        foreach (var list in inventorySlots.Select(slot => slot.List).Distinct())
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(character.Inventory, list,
                inventorySlots.Where(slot => slot.List == list).ToArray()), "quest_reward_inventory", Guard);
        if (commit.Currencies.Any(row => QuestCurrencyReward.HasBalanceNotice(row.Kind)))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(character.Inventory.AccountWallet),
                "quest_reward_cera", Guard);
        if (previous is not null)
            await SkillSwitchInventoryProjection.RefreshStateAsync(
                context,
                previous.SkillSwitchInventory,
                previous.Inventory,
                character,
                Guard,
                "quest_skill_switch_inventory");
        if ((commit.ExperienceReward.Amount != 0 || commit.GainedExperience != 0) &&
            CharacterExperienceNotice.ShouldPublish(commit.PreviousLevel))
        {
            var state = character.DungeonState ?? throw new InvalidOperationException("已提交任务成长缺少附属owner");
            var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience,
                state.ProficiencyLevel, state.ProficiencyExperience);
            var display = commit.ExperienceReward.Bonuses?.Display(Math.Min(commit.GainedExperience, commit.ExperienceReward.Amount)) ?? CharacterExperienceDisplay.Ordinary with
            { GrowthContractExperience = GrowthContractPolicy.AppliedBonus(commit.OrdinaryExperience,
                commit.ContractExperience, Math.Min(commit.GainedExperience, commit.ExperienceReward.Amount)) };
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning, auxiliary, display),
                "quest_reward_experience", Guard);
        }
        if (commit.PreviousQuests.Count - character.ActiveMainQuests.Length > 1 ||
            character.ActiveMainQuests.Any(row => commit.PreviousQuests.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(character.ActiveMainQuests), "quest_reward_conditions", Guard);
        if (!profession && !expertJob && character.Level == commit.PreviousLevel && character.GrowType == commit.PreviousGrowType &&
            (commit.TpAmount != 0 || commit.PreviousLearning is { } previousLearning &&
            SkillPackets.BindingsChanged(previousLearning, character.Learning)))
            await SkillProjection.PublishAsync(context, character, "quest_growth_skills", "quest_growth_combo", Guard);
        if (character.EquipmentSlotFlags != commit.PreviousEquipmentSlotFlags)
            await levelProjection.PlayerAsync(context,character,"quest_equipment_slots",Guard);
        await levelProjection.RefreshAsync(context, character, commit.PreviousLevel, "quest_level_committed", Guard, commit.PreviousGrowType,
            restoreCompletedQuests: false, previousLearning: commit.PreviousLearning);
        if (character.Level == commit.PreviousLevel && character.GrowType == commit.PreviousGrowType && projection is not null)
            await projection.RefreshAsync(context, character, Guard, restoreCompleted: false);
        // 匹配客户端空574会清完成集合；完整任务投影最后以356恢复真实完成记录。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character),"quest_completed_list",Guard);
        if ((commit.GoldMailId is not null || commit.ResourceMailId is not null) && receipt.Phase == DfoServer.Game.Session.SessionPhase.Town)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "quest_mail_arrived", Guard);
        if (expertJobs is not null && character.ExpertJobType is >= 1 and <= 4 && commit.ExpertJobType is >= 1 and <= 4)
            await context.Notify(NotiPacketType.EXPERT_JOB_INFO, ExpertJobPackets.Info(character, expertJobs, includeQualifications: true),
                "quest_expert_job_info", Guard);
        return CommandResult.Handled;
    }
}
