namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EquipmentTransmissionRequest(short TargetSlot, int TargetId,
    short DonorSlot, int DonorId);
