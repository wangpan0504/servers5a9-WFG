using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record VendingRequest(int Machine, int Group, short CoinSlot);
internal sealed record VendingGrantedItem(InventorySlot Slot, int Count);
internal sealed record VendingReceipt(short CoinSlot, int RemainingCoins, int ItemId, int Count, bool Contract,
    ImmutableArray<VendingGrantedItem> Items, short? CurrencySlot);
