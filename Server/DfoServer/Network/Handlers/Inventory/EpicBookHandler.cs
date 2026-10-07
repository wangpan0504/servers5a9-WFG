using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EpicBookHandler(EpicPieceCraftService craft, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.EPIC_BOOK_MAKE_ITEM, Handle, nameof(EpicBookHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        int equipmentId = EpicBookRequests.Craft(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(4) :
            context.Session.MutateInventory(character => craft.Craft(character, equipmentId), 4);
        await context.Reply(CmdPacketType.EPIC_BOOK_MAKE_ITEM, EpicBookPackets.Crafted(result), result.Accepted ? "epic_book_crafted" : "epic_book_craft_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
