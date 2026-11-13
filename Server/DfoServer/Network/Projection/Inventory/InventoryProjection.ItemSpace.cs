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
        await SynchronizeItemSpaceContentsAsync(context, snapshot);
        await context.Reply(CmdPacketType.SYNC_ITEM_SPACE, [1], "inventory_sync_completed", CanSend);
    }

    internal async Task SynchronizeItemSpaceContentsAsync(GameConnectionContext context, InventoryCommitReceipt snapshot,
        IReadOnlySet<InventoryListType>? lists = null)
    {
        bool CanSend() => context.Session.CanPublishInventory(snapshot);
        if (!CanSend()) return;
        var inventory = snapshot.Character.Inventory;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 恢复详情及完整容器；只读重放不重载数据库或改写资产。
        if (lists is null || lists.Contains(InventoryListType.Pet))
            await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(inventory), "inventory_sync_creatures", CanSend);
        if (lists is null || lists.Contains(InventoryListType.Main))
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(inventory, now), "inventory_sync_main", CanSend);
        foreach (var list in new[] { InventoryListType.Avatar, InventoryListType.Pet, InventoryListType.Equipment, InventoryListType.AccountCommon })
            if (lists is null || lists.Contains(list))
                await context.Notify(NotiPacketType.ITEM_LIST, town.SpecialFull(context, inventory, list, now), "inventory_sync_container", CanSend);
        if (lists is null || lists.Contains(InventoryListType.Avatar) || lists.Contains(InventoryListType.Equipment))
            await context.Notify(NotiPacketType.SRC_AVATAR_INFO_OF_CLONE, ClearAvatarPackets.Sources(inventory), "inventory_sync_clone_sources", CanSend);
        await RestoreLocksAsync(context, inventory, inventory.Items.Where(item => item.Position.List is
            (InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.Equipment or InventoryListType.AccountCommon) &&
            (lists is null || lists.Contains(item.Position.List)))
            .Select(item => item.Position), canSend: CanSend);
    }
}
