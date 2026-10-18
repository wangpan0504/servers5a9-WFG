using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult UseLimitCube(LimitCubeRequest request, InventoryLimitCubeService service,
        out LimitCubeReceipt? receipt, out string? rejection)
    {
        LimitCubeReceipt? plannedReceipt = null;
        string? plannedRejection = null;
        var result = PlanAndCommitInventory(character =>
            service.Apply(character, request, out plannedReceipt, out plannedRejection), 1);
        receipt = result.Accepted ? plannedReceipt : null;
        rejection = result.Accepted ? null : plannedRejection ?? "state-or-snapshot";
        return result;
    }
}
