namespace DfoServer.Game.Inventory.Models;

internal sealed record DisjointRequest(InventorySlot Position, short ToolSlot, int Context);
internal sealed record DisjointReward(short Slot, int ItemId, int Count);
internal sealed record DisjointReceipt(DisjointRequest Request, DisjointReward[] Rewards);
