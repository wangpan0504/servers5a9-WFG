using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult DisjointAvatar(AvatarDisjointRequest request, InventoryAvatarDisjointService service,
        out AvatarDisjointReceipt? receipt, out string? rejection)
    {
        AvatarDisjointReceipt? plannedReceipt = null;
        string? plannedRejection = null;
        var result = PlanAndCommitInventory(character =>
            service.Disjoint(character, request, out plannedReceipt, out plannedRejection), 19);
        receipt = result.Accepted ? plannedReceipt : null;
        rejection = result.Accepted ? null : plannedRejection ?? "state-or-snapshot";
        return result;
    }
}
