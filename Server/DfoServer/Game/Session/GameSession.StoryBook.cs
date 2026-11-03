using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult UseStoryBook(InventorySlot source, ushort questId, QuestCatalog quests, ItemCatalog items) =>
        PlanAndCommitInventory(character => QuestStoryBookService.Plan(character, source, questId, quests, items,
            towns.IsRoom(character.Position, QuestEligibility.Completed(character)) ? "[seria room]" : "[village]"),
            invalidStateError: 0, usedItem: source);
}
