namespace DfoServer.Game.Inventory.Models;

internal sealed record AvatarDisjointRequest(short SourceSlot, int ExpectedItemId)
{
    public InventorySlot Position => new(InventoryListType.Avatar, SourceSlot);
}

internal sealed record AvatarDisjointReward(short Slot, int ItemId, int Count);
internal sealed record AvatarDisjointReceipt(AvatarDisjointRequest Request, AvatarDisjointReward[] Rewards);
