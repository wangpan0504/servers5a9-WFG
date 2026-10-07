namespace DfoServer.Game.Inventory.Models;

// 操作内的只读索引，不挂到可用 with 派生的快照上，避免沿用旧位置或 UID。
internal sealed class InventoryIndex(InventorySnapshot snapshot)
{
    readonly Dictionary<InventorySlot, InventoryItem> slots = snapshot.Items.ToDictionary(item => item.Position);
    readonly Lazy<Dictionary<long, InventoryItem>> identities = new(() =>
        snapshot.Items.Where(item => item.Uid != 0).ToDictionary(item => item.Uid));

    public InventoryItem? Find(InventorySlot slot) => slots.GetValueOrDefault(slot);
    public InventoryItem? FindUid(long uid) => identities.Value.GetValueOrDefault(uid);
}
