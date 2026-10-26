namespace DfoServer.Game.Inventory.Models;

internal readonly record struct AvatarSocketRequest(short TargetSlot, int TargetItemId, short MaterialSlot)
{
    public InventorySlot Target => new(InventoryListType.Avatar, TargetSlot);
    public InventorySlot Material => new(InventoryListType.Main, MaterialSlot);
}
