namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EquipmentQualityRequest(short TargetSlot, int ItemId, short MaterialSlot,
    ushort ClientState, bool RandomOptions);
