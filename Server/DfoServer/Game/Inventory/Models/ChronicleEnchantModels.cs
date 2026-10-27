using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record ChronicleEnchantRequest(short MaterialSlot, int MaterialItemId, InventorySlot Target, int TargetItemId, byte Number);
internal sealed record ChronicleEnchantReceipt(short MaterialSlot, ushort MaterialRemaining, short TargetSlot, bool Succeeded,
    ImmutableArray<EnhancementRefund> Refunds);
internal sealed record ChronicleDestruction(InventorySnapshot After, ImmutableArray<EnhancementRefund> Refunds);
