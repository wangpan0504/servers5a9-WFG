namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EquipmentRandomOptionChangeRequest(ushort Slot, ushort Reserved, byte OptionIndex);
