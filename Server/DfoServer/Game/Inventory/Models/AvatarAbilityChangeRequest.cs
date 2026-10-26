namespace DfoServer.Game.Inventory.Models;

internal readonly record struct AvatarAbilityChangeRequest(short SourceSlot, int SourceId, short AvatarSlot, int AvatarId, byte Ability)
{
    public InventorySlot Source => new(InventoryListType.Main, SourceSlot);
    public InventorySlot Target => new(InventoryListType.Avatar, AvatarSlot);
}
