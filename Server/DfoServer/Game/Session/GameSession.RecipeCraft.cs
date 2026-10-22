using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult CraftRecipe(InventorySlot blueprint, InventoryRecipeCraftService service,
        out RecipeCraftReceipt? receipt)
    {
        RecipeCraftReceipt? planned = null;
        var result = PlanAndCommitInventory(character => service.Craft(character, blueprint, out planned),
            (ushort)InventoryRecipeCraftService.Rejection);
        receipt = result.Accepted ? planned : null;
        return result;
    }
}
