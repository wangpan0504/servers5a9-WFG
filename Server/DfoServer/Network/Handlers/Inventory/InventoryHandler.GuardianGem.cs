using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> UseGuardianGemAsync(GameConnectionContext context, Packet packet)
    {
        var request = GuardianGemRequests.Use(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(4) :
            context.Session.MutateInventory(character => inventory.UseGuardianGem(character, request), invalidStateError: 4);
        await context.Reply(CmdPacketType.USE_GEM, GuardianGemPackets.Used(result),
            result.Accepted ? "guardian_gem_committed" : "guardian_gem_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
