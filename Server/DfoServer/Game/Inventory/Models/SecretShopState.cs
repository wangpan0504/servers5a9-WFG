using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record SecretShopOffer(int ItemId, byte Option, int UnitGold, int Remaining);
internal sealed record SecretShopState(int Merchant, ImmutableArray<SecretShopOffer> Offers)
{
    public bool Available => Merchant != 1000;
}
internal sealed record SecretShopPurchase(int ItemId, int Count);
internal sealed record SecretShopPurchaseReceipt(InventorySlot Position, ItemCore Core, int Remaining);
internal sealed record SecretShopPurchasePlan(SecretShopState After, InventoryResult Inventory,
    SecretShopPurchaseReceipt? Purchase = null);
