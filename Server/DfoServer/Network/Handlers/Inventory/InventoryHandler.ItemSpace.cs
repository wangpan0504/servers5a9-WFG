using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> SynchronizeItemSpaceAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 1)
        {
            log?.Write("inventory_sync_rejected", new { Connection = context.Id, Reason = "malformed" });
            return CommandResult.Handled;
        }
        byte selector = InventoryRequests.SynchronizeItemSpace(packet.Body);
        var snapshot = context.Session.ReadInventoryForSynchronization();
        if (snapshot is not null)
            await projection.SynchronizeItemSpaceAsync(context, snapshot);
        log?.Write("inventory_sync", new { Connection = context.Id, Selector = selector,
            Outcome = snapshot is null ? "not_ready" : "snapshot_queued" });
        return CommandResult.Handled;
    }
}
