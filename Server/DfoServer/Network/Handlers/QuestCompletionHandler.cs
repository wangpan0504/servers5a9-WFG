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
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class QuestCompletionHandler(QuestCatalog quests, QuestRewardParameterCatalog parameters,
    ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, CharacterGrowthCatalog growth,
    ServerLog log, CharacterLevelProjection levelProjection, QuestProjection? projection = null, CreatureProjection? creatures = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.FINISH_QUEST, FinishAsync, nameof(QuestCompletionHandler));

    private async Task<CommandResult> FinishAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TryFinish(packet.Body, out var request) ||
            request is not { Reserved: ushort.MaxValue } ||
            context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var previousInventory = context.Owner.Execute(() => context.Session.Selected?.Inventory);
        var result = context.Session.FinishQuest(request.QuestId, request.RewardSelector, quests, parameters, items, goldLimits, pieces, growth, request.Multiplier);
        log.Write("quest_completion", new { Connection = context.Id, Quest = request.QuestId, result.Outcome,
            Accepted = result.Outcome == QuestFinishOutcome.Committed, Failure = result.Failure.ToString(),
            result.Receipt?.Commit.AcceptanceId, result.Receipt?.Commit.Clear?.InstanceId,
            result.Receipt?.Commit.ExperienceReward, result.Receipt?.Commit.GainedExperience, result.Receipt?.Commit.Duplicate,
            result.Receipt?.Commit.DirectGoldAmount, result.Receipt?.Commit.MailedGoldAmount, result.Receipt?.Commit.GoldMailId });
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
                commit.Consumed.Select(row => new QuestConsumedItemProjection(row.Position.Index, row.Count)).ToArray(), commit.DirectGoldAmount, rewards,
                commit.ProfessionGrowType,commit.Currencies.Select(row => new QuestCurrencyRewardProjection(
                    ItemSlotBoundService.CurrencySlot(row.Kind) ?? throw new InvalidOperationException("钱包奖励缺少已确认持有域"),
                    row.Kind == DfoServer.Game.Currency.CurrencyKind.ReviveCoin ? 1 : row.ItemId,row.Count)).ToArray(),commit.TpAmount,commit.HellChallenge,
                creatureEvolution: QuestCreatureReward.Evolution(quests.Get(commit.QuestId)));
        // 转职/觉醒先装载已提交技能快照，ACK34会据此重建本地技能；沿S4A21的前置通知顺序。
        bool profession = commit.ProfessionGrowType is not null;
        if (profession)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "quest_profession_skills", Guard);
        await context.Reply(CmdPacketType.FINISH_QUEST, acknowledgement, "quest_completion_committed", Guard);
        if (creatures is not null && previousInventory is not null && commit.ChangedSlots.Contains(Game.Creatures.CreatureRules.EquippedSlot))
            await creatures.ChangedAsync(context, previousInventory, character, receipt.Phase, Guard);
        // ACK34先消费真实增量，再投影完整Core及绝对成长；不在最终库存之后重放增量奖励。
        if (commit.ChangedSlots.Length != 0 || commit.DirectGoldAmount != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(character.Inventory, InventoryListType.Main,
                commit.ChangedSlots.Concat(commit.DirectGoldAmount == 0 ? [] : new[] { new InventorySlot(InventoryListType.Main, 0) }).ToArray()),
                "quest_reward_inventory", Guard);
        if (commit.ExperienceReward.Amount != 0)
        {
            var state = character.DungeonState ?? throw new InvalidOperationException("已提交任务成长缺少附属owner");
            var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience,
                state.ProficiencyLevel, state.ProficiencyExperience);
            var display = CharacterExperienceDisplay.Ordinary with
            { GrowthContractExperience = GrowthContractPolicy.AppliedBonus(commit.ExperienceReward.Amount - commit.ExperienceReward.GrowthContractExperience,
                commit.ExperienceReward.GrowthContractExperience, commit.GainedExperience) };
            await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning, auxiliary, display),
                "quest_reward_experience", Guard);
        }
        if (commit.PreviousQuests.Count - character.ActiveMainQuests.Length > 1 ||
            character.ActiveMainQuests.Any(row => commit.PreviousQuests.Any(old => old.AcceptanceId == row.AcceptanceId && old.ConditionState != row.ConditionState)))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(character.ActiveMainQuests), "quest_reward_conditions", Guard);
        if (!profession && (commit.TpAmount != 0 || commit.PreviousLearning is { } previous && SkillPackets.BindingsChanged(previous, character.Learning)))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "quest_growth_skills", Guard);
        if (character.EquipmentSlotFlags != commit.PreviousEquipmentSlotFlags)
            await levelProjection.PlayerAsync(context,character,"quest_equipment_slots",Guard);
        await levelProjection.RefreshAsync(context, character, commit.PreviousLevel, "quest_level_committed", Guard, commit.PreviousGrowType,
            restoreCompletedQuests: false);
        if (character.Level == commit.PreviousLevel && character.GrowType == commit.PreviousGrowType && projection is not null)
            await projection.RefreshAsync(context, character, Guard, restoreCompleted: false);
        // 匹配客户端空574会清完成集合；完整任务投影最后以356恢复真实完成记录。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character),"quest_completed_list",Guard);
        if (commit.GoldMailId is not null && receipt.Phase == DfoServer.Game.Session.SessionPhase.Town)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "quest_gold_mail_arrived", Guard);
        return CommandResult.Handled;
    }
}
