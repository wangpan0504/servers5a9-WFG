using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
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
    ServerLog log, CharacterLevelProjection levelProjection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.FINISH_QUEST, FinishAsync, nameof(QuestCompletionHandler));

    private async Task<CommandResult> FinishAsync(GameConnectionContext context, Packet packet)
    {
        if (!QuestRequests.TryFinish(packet.Body, out var request) ||
            request is not { QuestId: 3145, RewardSelector: ushort.MaxValue, Multiplier: 1, Reserved: ushort.MaxValue } ||
            context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var result = context.Session.FinishSingleClearQuest(request.QuestId, quests, parameters, items, goldLimits, pieces, growth);
        log.Write("quest_completion", new { Connection = context.Id, result.Outcome,
            result.Receipt?.Commit.Clear.AcceptanceId, result.Receipt?.Commit.Clear.InstanceId,
            result.Receipt?.Commit.ExperienceReward, result.Receipt?.Commit.Duplicate });
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
        var state = character.DungeonState ?? throw new InvalidOperationException("已提交任务成长缺少附属owner");
        var auxiliary = new CharacterGrowthAuxiliaryState(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience,
            state.ProficiencyLevel, state.ProficiencyExperience);
        var display = new CharacterExperienceDisplay(commit.ExperienceReward.Amount, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0, 0, 0, 0);
        await context.Reply(CmdPacketType.FINISH_QUEST, QuestPackets.FinishSingleClearMap(request.QuestId, commit.ExperienceReward.Amount,
            commit.Rewards.Select(row => new QuestRewardItemProjection(row.Item, row.GrantedCount)).ToArray()), "quest_completion_committed", Guard);
        // ACK34先消费真实增量，再投影完整Core及绝对成长；不在最终库存之后重放增量奖励。
        await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(character.Inventory, InventoryListType.Main,
            commit.Rewards.Select(row => row.Item.Position).ToArray()), "quest_reward_inventory", Guard);
        await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning, auxiliary, display),
            "quest_reward_experience", Guard);
        if (commit.PreviousLearning is { } previous && SkillPackets.BindingsChanged(previous, character.Learning))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(character.Learning), "quest_growth_skills", Guard);
        await levelProjection.RefreshAsync(context, character, commit.PreviousLevel, "quest_level_committed", Guard);
        return CommandResult.Handled;
    }
}
