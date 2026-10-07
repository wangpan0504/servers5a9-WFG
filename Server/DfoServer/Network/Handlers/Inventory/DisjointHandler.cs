using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class DisjointHandler(InventoryDisjointService disjoint, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.DISJOINT_ITEM, Handle, nameof(DisjointHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = DisjointRequests.Read(packet.Body);
        DisjointReceipt? receipt = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.MutateInventory(character => disjoint.Disjoint(character, request, out receipt), 19);
        await context.Reply(CmdPacketType.DISJOINT_ITEM, DisjointPackets.Ack(result, receipt), result.Accepted ? "equipment_disjoint" : "equipment_disjoint_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
