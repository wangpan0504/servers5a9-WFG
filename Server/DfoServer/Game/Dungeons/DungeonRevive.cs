using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal static class DungeonRevive
{
    public static InventoryResult Plan(Character character)
    {
        var before = character.Inventory;
        return before.ReviveCoins > 0
            ? new(true, 0, new InventoryPlan(before, before with { ReviveCoins = before.ReviveCoins - 1 }, "dungeon_revive"))
            : InventoryResult.Reject();
    }
}
