using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class QuestCompletionHandler
{
    async Task<CommandResult> FinishHopeAsync(GameConnectionContext context, QuestFinishRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected,context.Session.SelectionEpoch,context.Session.Phase));
        var receipt = context.Session.FinishOperationHope(request.QuestId,request.RewardSelector,request.Multiplier,
            quests,parameters,items,goldLimits,pieces,growth);
        if (receipt is null)
        {
            await context.Reply(CmdPacketType.FINISH_QUEST,QuestPackets.Rejected(19),"operation_hope_reward_rejected",
                () => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected,before.Selected) &&
                    context.Session.SelectionEpoch == before.SelectionEpoch && context.Session.Phase == before.Phase));
            return CommandResult.Handled;
        }
        if (!receipt.Commit.ProjectionCurrent) return CommandResult.CloseConnection;
        await PublishHopeAsync(context,receipt,"operation_hope_reward_committed",automatic:false);
        return CommandResult.Handled;
    }

    // 翻牌完成后由结算路径调用；找不到已选免费牌或任务条件未达成时静默跳过。
    internal async Task<bool> CompleteHopeAfterCardAsync(GameConnectionContext context)
    {
        var receipt = context.Session.FinishOperationHopeAfterCard(quests,parameters,items,goldLimits,pieces,growth);
        if (receipt is null || !receipt.Commit.ProjectionCurrent) return false;
        await PublishHopeAsync(context,receipt,"operation_hope_reward_auto_committed",automatic:true);
        return true;
    }

    async Task PublishHopeAsync(GameConnectionContext context, OperationHopeFinishReceipt receipt, string action, bool automatic)
    {
        bool Guard() => context.Session.CanPublishOperationHope(receipt);
        var commit = receipt.Commit;
        log.Write("operation_hope_reward",new { Connection = context.Id, Quest = receipt.QuestId,
            receipt.Identity.InstanceId, commit.Duplicate, commit.GainedExperience, Automatic = automatic });
        // 原资源reward35与item共用行；线端mode0发真实19B奖励，原grade29不写普通完成位。
        await context.Reply(CmdPacketType.FINISH_QUEST,QuestPackets.Finish(receipt.QuestId,2,commit.GainedExperience,[],0,
            commit.Rewards.Select(row => new QuestRewardItemProjection(row.Item,row.GrantedCount)).ToArray()),
            action,Guard);
        foreach (var list in commit.ChangedSlots.Select(slot => slot.List).Distinct())
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(commit.Character.Inventory,list,
                commit.ChangedSlots.Where(slot => slot.List == list).ToArray()),"operation_hope_reward_inventory",Guard);
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(commit.Character.ActiveMainQuests),
            "operation_hope_reward_active",Guard);
        // 最后一个活动槽领奖产生空574，会清客户端完成集合；切回城镇前恢复真实开路与阶段位。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(commit.Character),
            "operation_hope_reward_completed",Guard);
    }
}
