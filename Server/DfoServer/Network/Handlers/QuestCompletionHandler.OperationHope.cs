using DfoServer.Game.Inventory.Models;
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
        bool Guard() => context.Session.CanPublishOperationHope(receipt);
        var commit = receipt.Commit;
        log.Write("operation_hope_reward",new { Connection = context.Id, request.QuestId,
            receipt.Identity.InstanceId, commit.Duplicate, commit.GainedExperience });
        // 原资源reward35与item共用行；线端mode0发真实19B奖励，原grade29不写普通完成位。
        await context.Reply(CmdPacketType.FINISH_QUEST,QuestPackets.Finish(request.QuestId,2,commit.GainedExperience,[],0,
            commit.Rewards.Select(row => new QuestRewardItemProjection(row.Item,row.GrantedCount)).ToArray()),
            "operation_hope_reward_committed",Guard);
        foreach (var list in commit.ChangedSlots.Select(slot => slot.List).Distinct())
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(commit.Character.Inventory,list,
                commit.ChangedSlots.Where(slot => slot.List == list).ToArray()),"operation_hope_reward_inventory",Guard);
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(commit.Character.ActiveMainQuests),
            "operation_hope_reward_active",Guard);
        return CommandResult.Handled;
    }
}
