using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal readonly record struct AvatarEmblemEntry(short MaterialSlot, int MaterialItemId, byte SocketIndex)
{
    public InventorySlot Material => new(InventoryListType.Main, MaterialSlot);
}

internal sealed record AvatarEmblemRequest(InventorySlot Target, int TargetItemId,
    ImmutableArray<AvatarEmblemEntry> Emblems);

internal readonly record struct AvatarEmblemRemaining(short Slot, int Count);

internal sealed record AvatarEmblemReceipt(InventorySlot Target, ImmutableArray<AvatarEmblemRemaining> Materials);
