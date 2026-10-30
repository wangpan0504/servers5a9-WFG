using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class VendingHandler(InventoryVendingService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_VENDING_MACHINE, Handle, nameof(VendingHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        VendingRequest? request = null;
        VendingReceipt? receipt = null;
        InventoryResult result;
        try
        {
            request = VendingRequests.Read(packet.Body);
            result = context.BlocksTownActions ? InventoryResult.Reject(1) : context.Session.UseVendingMachine(service, request, out receipt);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(1);
        }
        bool Guard() => result.Receipt is { } committed ? context.Session.CanPublishInventory(committed) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("vending_machine", new { Connection = context.Id, request?.Machine, request?.Group, request?.CoinSlot,
            result.Accepted, result.Error, Reward = receipt, Revision = result.Receipt?.Character.Revision });
        // 0237AD77先查原硬币槽，成功ACK必须先于源槽清除及奖励总数差量。
        await context.Reply(CmdPacketType.USE_VENDING_MACHINE, VendingPackets.Opened(result, receipt),
            result.Accepted ? "vending_committed" : "vending_rejected", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
