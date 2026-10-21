using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

// CMD271 UPGRADE_CHRONICLE：NPC 出售的设计图把 95 传说装备升级为同部位史诗。
// 无参考端，请求结构与成功应答都是实机抓包确认的；失败应答形态待核对。
internal sealed class UpgradeChronicleHandler(InventoryUpgradeChronicleService upgrades, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
        => commands.Register(CmdPacketType.UPGRADE_CHRONICLE, Upgrade, nameof(UpgradeChronicleHandler));

    async Task<CommandResult> Upgrade(GameConnectionContext context, Packet packet)
    {
        var request = UpgradeChronicleRequests.Upgrade(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(InventoryUpgradeChronicleService.Rejection) :
            context.Session.MutateInventory(character => upgrades.Upgrade(character, request.Blueprint, request.BlueprintItemId,
                request.Target, request.TargetItemId), (ushort)InventoryUpgradeChronicleService.Rejection);
        await context.Reply(CmdPacketType.UPGRADE_CHRONICLE, UpgradeChroniclePackets.Result(result),
            result.Accepted ? "upgrade_chronicle" : "upgrade_chronicle_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
