namespace DfoServer.Game.Inventory.Models;

internal readonly record struct AvatarDyeRequest(short SourceSlot, short AvatarSlot)
{
    public InventorySlot Source => new(InventoryListType.Main, SourceSlot);
    public InventorySlot Target => new(InventoryListType.Avatar, AvatarSlot);
}
