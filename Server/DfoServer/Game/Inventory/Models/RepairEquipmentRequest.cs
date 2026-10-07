namespace DfoServer.Game.Inventory.Models;

internal sealed record RepairEquipmentRequest(InventorySlot Position, short RepairItemSlot, ushort Mode, bool Quick);
