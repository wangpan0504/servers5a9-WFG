using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult CompoundGuild(GuildCompoundKind kind, GuildCompoundRequest request,
        InventoryGuildCompoundService service, out GuildCompoundReceipt? receipt, out string? rejection)
    {
        GuildCompoundReceipt? plannedReceipt = null;
        string? plannedRejection = null;
        var result = PlanAndCommitInventory(character =>
            service.Apply(character, kind, request, out plannedReceipt, out plannedRejection));
        receipt = result.Accepted ? plannedReceipt : null;
        rejection = result.Accepted ? null : plannedRejection ?? "state-or-snapshot";
        return result;
    }
}
