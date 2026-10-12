using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class BoosterHandler(InventoryBoosterService booster, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.USE_BOOSTER_ITEM, Handle, nameof(BoosterHandler));
        commands.Register(CmdPacketType.OPEN_CERAPACKAGE, HandleCeraPackage, nameof(BoosterHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        // 独立 detail 的附加选择段仍留给对应完整合同，不截断为普通物品选择。
        if (packet.Body.Length > 6 && !BoosterRequests.IsItemSelection(packet.Body))
            return CommandResult.Unhandled;
        BoosterOpenRequest request = BoosterRequests.Read(packet.Body);
        BoosterReceipt? receipt = null;
        InventoryResult result;
        try
        {
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(19) :
                context.Session.PlanAndCommitInventory(character => booster.Open(character, request, out receipt), 19, requireTown: false);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        await context.Reply(CmdPacketType.USE_BOOSTER_ITEM, BoosterPackets.Opened(result, receipt),
            result.Accepted ? "booster_opened" : "booster_open_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> HandleCeraPackage(GameConnectionContext context, Packet packet)
    {
        CeraPackageOpenRequest request;
        try
        {
            request = CeraPackageRequests.Read(packet.Body);
        }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.OPEN_CERAPACKAGE, CeraPackagePackets.Opened(InventoryResult.Reject(19), null),
                "cera_package_malformed");
            return CommandResult.Handled;
        }

        BoosterReceipt? receipt = null;
        InventoryResult result;
        try
        {
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(4) :
                context.Session.PlanAndCommitInventory(character => booster.OpenCeraPackage(character, request, out receipt), 19, requireTown: false);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(19);
        }
        await context.Reply(CmdPacketType.OPEN_CERAPACKAGE, CeraPackagePackets.Opened(result, request),
            result.Accepted ? "cera_package_opened" : "cera_package_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
