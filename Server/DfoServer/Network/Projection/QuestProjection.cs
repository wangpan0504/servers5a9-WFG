using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 共享只读目录与当前角色快照；不保存另一份在线资格或任务状态。
internal sealed class QuestProjection(QuestCatalog quests, QuestRewardParameterCatalog? parameters = null,
    ItemCatalog? items = null, GoldLimitCatalog? goldLimits = null, EpicPieceCatalog? pieces = null, CharacterGrowthCatalog? growth = null)
{
    public Task CreatureChangedAsync(GameConnectionContext context, Character before, Character after, Func<bool> canSend) =>
        QuestEligibility.CreatureCandidatesChanged(before, after, quests)
            ? RefreshAsync(context, after, canSend) : Task.CompletedTask;

    public async Task<bool> CloseReadyChildrenAsync(GameConnectionContext context, bool refreshList = false)
    {
        if (parameters is null || items is null || goldLimits is null || pieces is null || growth is null) return false;
        var before = context.Session.Selected?.Inventory;
        var receipt = context.Session.FinishReadyQuestChildren(quests,parameters,items,goldLimits,pieces,growth);
        if (receipt is null) return false;
        if (!receipt.Commit.ProjectionCurrent) throw new InvalidOperationException("子任务已提交，需要重连恢复");
        bool Guard() => context.Session.CanPublishQuestCompletion(receipt);
        var character = receipt.Commit.Character;
        // 重选恢复可能关闭多个寻物子任务，按全部提交前后的库存差量同步材料扣除。
        var changes = before is null ? null : new InventoryPlan(before,character.Inventory,"已达成子任务材料同步");
        var slots = (changes?.ChangedSlots ?? receipt.Commit.ChangedSlots)
            .Concat((changes?.ChangedCurrencies ?? []).Select(ItemSlotBoundService.CurrencySlot)
                .Where(slot => slot.HasValue).Select(slot => new InventorySlot(InventoryListType.Main,slot!.Value)))
            .Distinct().ToArray();
        if (slots.Length != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(character.Inventory,InventoryListType.Main,[.. slots]),"quest_ready_children_inventory",Guard);
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(character.ActiveMainQuests),"quest_ready_children_progress",Guard);
        if (refreshList) await RefreshAsync(context,character,Guard, restoreCompleted: false);
        // 匹配客户端空574会清完成集合，356须放在完整任务投影最后。
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
