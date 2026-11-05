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
    public const string HellTerrainBypassText = "已优先进入深渊模式地下城， 部分任务可能无法在本次通关中完成";

    public static Task DungeonTerrainBypassAsync(GameConnectionContext context, DfoServer.Game.Dungeons.DungeonEntryPlan entry,
        Func<bool> guard) => !entry.BypassedQuestTerrain.IsEmpty
        ? context.NotifyNotice(ServerNoticeDisplay.Chat, HellTerrainBypassText, "hell_quest_terrain_bypassed", guard)
        : Task.CompletedTask;

    public static async Task PublishConditionsAsync(GameConnectionContext context,
        IReadOnlyList<ActiveMainQuest> changed, Func<bool> guard, string action)
    {
        // 574销毁并重建活动对象，不能执行原活动对象的达成回调；条件变化统一用33原位安装。
        foreach (var row in changed)
            await context.Reply(CmdPacketType.SET_QUEST_TRIGGER, QuestPackets.ClearTrigger(row.QuestId,row.ConditionState),
                action,guard);
    }

    public static async Task ProgressAsync(GameConnectionContext context, IReadOnlyList<ActiveMainQuest> previous,
        Character character, Func<bool> guard, string action, bool restoreCompleted = true)
    {
        var active = character.ActiveMainQuests;
        bool sameAcceptances = previous.Count == active.Length && active.All(row => previous.Any(old =>
            old.Slot == row.Slot && old.QuestId == row.QuestId && old.AcceptanceId == row.AcceptanceId));
        if (sameAcceptances)
        {
            await PublishConditionsAsync(context,active.Where(row => previous.Any(old =>
                old.Slot == row.Slot && old.AcceptanceId == row.AcceptanceId && old.QuestId == row.QuestId && old.ConditionState != row.ConditionState)).ToArray(),
                guard,action);
            return;
        }
        // 接取槽增删仍需完整结构；空574会清完成集合，随后恢复同一提交的历史。
        await context.Notify(NotiPacketType.ACCEPTED_QUEST,QuestPackets.ActiveList(active),action,guard);
        if (restoreCompleted)
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST,TownPackets.CompletedQuests(character),action + "_history",guard);
    }

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
        if (receipt.BattlePassMissionAt is { } at)
            await BattlePassMissionProjection.RefreshAsync(context, at);
        return true;
    }

    public async Task InitializeAsync(GameConnectionContext context, Character character)
    {
        await context.Notify(NotiPacketType.ACCEPTABLE_QUEST_LIST, Prepare(context, character), "quest_list_initialize");
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character), "quest_history_initialize");
        // 同连接重新选角也须用空703清除上一角色视图，不能只恢复普通356。
        await StoryBooksAsync(context, character, includeEmpty: true);
    }

    public async Task RefreshAsync(GameConnectionContext context, Character character, Func<bool> canSend, bool restoreCompleted = true)
    {
        if (!canSend())
            return;
        byte[] body = Prepare(context, character);
        await context.Notify(NotiPacketType.ACCEPTABLE_QUEST_LIST, body, "quest_list_refresh", canSend);
        // 21会重建任务界面列表；先保留场景所需的完成位，再在列表刷新后恢复完成历史。
        if (restoreCompleted)
        {
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(character), "quest_history_refresh", canSend);
            await StoryBooksAsync(context, character, canSend);
        }
    }

    public async Task StoryBooksAsync(GameConnectionContext context, Character character, Func<bool>? canSend = null, bool includeEmpty = false)
    {
        var completed = QuestEligibility.Completed(character);
        int[] books = quests.StoryBookQuestIds.Where(completed.Contains).ToArray();
        // 正常任务业务不撤销已完成故事；切角的空视图在Initialize中明确恢复。
        if (books.Length == 0 && !includeEmpty) return;
        var captured = context.Owner.Execute(() => (context.Session.SelectionEpoch, context.Session.Phase));
        bool Guard() => canSend?.Invoke() != false && context.Owner.Execute(() =>
            context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase &&
            ReferenceEquals(context.Session.Selected, character));
        await context.Notify(NotiPacketType.STORY_BOOK_INFO, QuestPackets.StoryBookInfo(books), "story_book_completed_info", Guard);
    }

    private byte[] Prepare(GameConnectionContext context, Character character) => QuestPackets.AvailableList(character.Level,
        QuestEligibility.Candidates(character, quests,
            strongway2019Active: context.Session.Strongway2019QuestActive,
            epicRoad2019Active: context.Session.EpicRoad2019QuestActive));
}
