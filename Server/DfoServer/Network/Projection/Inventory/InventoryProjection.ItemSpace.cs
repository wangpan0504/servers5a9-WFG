using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed partial class InventoryProjection
{
    public async Task SynchronizeItemSpaceAsync(GameConnectionContext context, InventoryCommitReceipt snapshot)
    {
        bool CanSend() => context.Session.CanPublishInventory(snapshot);
        if (!CanSend()) return;
        var inventory = snapshot.Character.Inventory;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 先恢复详情及完整容器，最后解除原生同步等待；只读重放不重载数据库或改写资产。
        await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(inventory), "inventory_sync_creatures", CanSend);
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(inventory, now), "inventory_sync_main", CanSend);
        foreach (var list in new[] { InventoryListType.Avatar, InventoryListType.Pet, InventoryListType.Equipment })
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(inventory, list, now), "inventory_sync_container", CanSend);
        await context.Notify(NotiPacketType.SRC_AVATAR_INFO_OF_CLONE, ClearAvatarPackets.Sources(inventory), "inventory_sync_clone_sources", CanSend);
        await RestoreLocksAsync(context, inventory, inventory.Items.Where(item => item.Position.List is
            InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.Equipment)
            .Select(item => item.Position), canSend: CanSend);
        await context.Reply(CmdPacketType.SYNC_ITEM_SPACE, [1], "inventory_sync_completed", CanSend);
    }
}
