using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestStoryBookService
{
    public static InventoryResult Plan(Character character, InventorySlot source, ushort questId, QuestCatalog quests,
        ItemCatalog items, string place, TimeProvider? time = null)
    {
        try
        {
            var item = character.Inventory.Find(source);
            var book = item is null ? null : items.ActionConsumable(item.Core.ItemId);
            if (book is not { Label: "[story book]", Parameters: [> 0 and < 30000] } || book.Parameters[0] != questId ||
                !quests.TryGet(questId, out var quest))
                return Reject("story-book-source-or-quest-mismatch");
            var eligibility = QuestEligibility.Evaluate(character, quest, quests);
            if (!eligibility.Eligible || !eligibility.Visible) return Reject(eligibility.Reason);
            QuestCoverage.RequireOrdinaryDeclarations(quest);
            if (!QuestTargetRules.Supports(quest, quests)) return Reject("unsupported-story-book-target");
            // 当前故事任务沿既有21候选、31接取及账号完成记录恢复，不另存平行解锁状态。
            // 消费只开启原生选择框；不得在确认前占活动槽、发接取成功或标记已完成。
            return InventoryActionConsumableService.Plan(character, source, book, place, time ?? TimeProvider.System);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        {
            return Reject(error.Message);
        }
    }

    static InventoryResult Reject(string reason) => InventoryResult.Reject(0) with { RejectReason = reason };
}
