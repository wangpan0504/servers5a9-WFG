using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class BoosterHandler(InventoryBoosterService booster, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_BOOSTER_ITEM, Handle, nameof(BoosterHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        // 独立 detail 的附加选择段仍留给对应完整合同，不截断为普通物品选择。
        if (packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body))
            return CommandResult.Unhandled;
        var request = BoosterRequests.Read(packet.Body);
        BoosterReceipt? receipt = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(character => booster.Open(character, request, out receipt), 19);
        await context.Reply(CmdPacketType.USE_BOOSTER_ITEM, BoosterPackets.Opened(result, receipt),
            result.Accepted ? "booster_opened" : "booster_open_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
