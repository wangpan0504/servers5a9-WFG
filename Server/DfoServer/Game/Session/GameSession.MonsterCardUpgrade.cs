using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult UpgradeMonsterCard(MonsterCardUpgradeRequest request, InventoryMonsterCardUpgradeService service,
        out MonsterCardUpgradeReceipt? receipt, out string? rejection)
    {
        MonsterCardUpgradeReceipt? plannedReceipt = null;
        string? plannedRejection = null;
        var result = PlanAndCommitInventory(character =>
            service.Apply(character, request, out plannedReceipt, out plannedRejection), 22);
        receipt = result.Accepted ? plannedReceipt : null;
        rejection = result.Accepted ? null : plannedRejection ?? "state-or-snapshot";
        return result;
    }
}
