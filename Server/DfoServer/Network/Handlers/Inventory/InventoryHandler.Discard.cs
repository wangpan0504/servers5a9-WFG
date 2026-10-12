using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> DiscardAsync(GameConnectionContext context, Packet packet)
    {
        var request = InventoryRequests.Discard(packet.Body);
        var result = context.Session.DiscardFormalDungeonItem(request, inventory);
        if (result.Error != 0)
        {
            await context.Reply(CmdPacketType.DROP_ITEM, InventoryDiscardPackets.Rejected(request.List, result.Error), "inventory_discard_rejected");
            return CommandResult.Handled;
        }
        var committed = result.Inventory ?? throw new InvalidOperationException("已提交丢弃缺少库存结果");
        var receipt = committed.Receipt ?? throw new InvalidOperationException("已提交丢弃缺少实际角色回执");
        var ground = result.Ground ?? throw new InvalidOperationException("已提交丢弃缺少地面对象");
        bool Guard() => context.Session.CanPublishInventory(receipt);
        await context.Reply(CmdPacketType.DROP_ITEM, InventoryDiscardPackets.Accepted(request), "inventory_discard_committed", Guard);
        await context.Notify(NotiPacketType.DROP_ITEM, InventoryDiscardPackets.Ground(receipt.Character.Id, ground), "inventory_discard_ground", Guard);
        await projection.RefreshAsync(context, committed);
        return CommandResult.Handled;
    }
}
