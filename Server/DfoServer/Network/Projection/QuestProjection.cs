using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 共享只读目录与当前角色快照；不保存另一份在线资格或任务状态。
internal sealed class QuestProjection(QuestCatalog quests, QuestRewardParameterCatalog? parameters = null,
    ItemCatalog? items = null, GoldLimitCatalog? goldLimits = null, EpicPieceCatalog? pieces = null, CharacterGrowthCatalog? growth = null)
{
    public async Task<bool> CloseReadyChildrenAsync(GameConnectionContext context, bool refreshList = false)
    {
        if (parameters is null || items is null || goldLimits is null || pieces is null || growth is null) return false;
        var receipt = context.Session.FinishReadyQuestChildren(quests,parameters,items,goldLimits,pieces,growth);
        if (receipt is null) return false;
        if (!receipt.Commit.ProjectionCurrent) throw new InvalidOperationException("子任务已提交，需要重连恢复");
        bool Guard() => context.Session.CanPublishQuestCompletion(receipt);
        var character = receipt.Commit.Character;
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(character.ActiveMainQuests),"quest_ready_children_progress",Guard);
        if (refreshList) await RefreshAsync(context,character,Guard, restoreCompleted: false);
        // V31空574会清完成集合，356须放在完整任务投影最后。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(character),"quest_ready_children_completed",Guard);
        return true;
    }

    public async Task InitializeAsync(GameConnectionContext context, Character character)
    {
        await context.Notify(NotiPacketType.ACCEPTABLE_QUEST_LIST, Prepare(character), "quest_list_initialize");
        if (character.CompletedQuests.Length != 0 || character.AccountCompletedQuests.Length != 0)
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character), "quest_history_initialize");
    }

    public async Task RefreshAsync(GameConnectionContext context, Character character, Func<bool> canSend, bool restoreCompleted = true)
    {
        if (!canSend())
            return;
        byte[] body = Prepare(character);
        await context.Notify(NotiPacketType.ACCEPTABLE_QUEST_LIST, body, "quest_list_refresh", canSend);
        // 21会重建任务界面列表；先保留场景所需的完成位，再在列表刷新后恢复完成历史。
        if (restoreCompleted && (character.CompletedQuests.Length != 0 || character.AccountCompletedQuests.Length != 0))
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character), "quest_history_refresh", canSend);
    }

    private byte[] Prepare(Character character) => QuestPackets.AvailableList(character.Level, QuestEligibility.Candidates(character, quests));
}
