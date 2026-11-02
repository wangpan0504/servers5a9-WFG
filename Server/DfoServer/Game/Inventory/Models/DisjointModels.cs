namespace DfoServer.Game.Inventory.Models;

// 服务端业务入口，不是CMD26的未知context或客户端opcode。
internal enum DisjointMode { Ordinary, OwnMachine, OtherMachine, HelperAuthorized }

internal sealed record DisjointRequest(InventorySlot Position, short ToolSlot, int Context);
internal sealed record DisjointReward(short Slot, int ItemId, int Count);
internal sealed record DisjointReceipt(DisjointRequest Request, DisjointReward[] Rewards);
