namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EquipmentUpgradeRequest(short Slot, int ItemId, bool Amplify,
    byte Upgrade, byte GenuineUpgrade, bool InheritEnchant, int TargetId, bool InheritEmblems);
internal readonly record struct EquipmentFusionRequest(short SourceSlot, int SourceId, short TargetSlot, int TargetId);
internal readonly record struct DarkRevelationRequest(short Slot, int ItemId, byte PowerSlot, byte Ability);
