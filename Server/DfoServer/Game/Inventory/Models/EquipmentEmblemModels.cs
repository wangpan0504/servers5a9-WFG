using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct EquipmentEmblemEntry(short MaterialSlot, int MaterialItemId, byte SocketIndex)
{
    public InventorySlot Material => new(InventoryListType.Main, MaterialSlot);
}

internal sealed record EquipmentEmblemRequest(short TargetSlot, int TargetItemId,
    ImmutableArray<EquipmentEmblemEntry> Emblems);

internal readonly record struct EquipmentSocketRequest(short TargetSlot, int TargetItemId, short MaterialSlot)
{
    public InventorySlot Material => new(InventoryListType.Main, MaterialSlot);
}

internal readonly record struct EquipmentEmblemRemaining(short Slot, int Count);

internal sealed record EquipmentEmblemReceipt(InventorySlot Target,
    ImmutableArray<EquipmentEmblemRemaining> Materials);

internal sealed record EquipmentSocketReceipt(InventorySlot Target, short MaterialSlot);
