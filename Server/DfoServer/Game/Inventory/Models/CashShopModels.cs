namespace DfoServer.Game.Inventory.Models;

internal sealed record CashShopPurchaseItem(int CommodityId, byte AttributeValue, byte TypeIndex = 0, byte PaymentMode = 0,
    byte FirstOptionCount = 0, byte SecondOptionCount = 0, uint RelatedItemId = 0, ushort RelatedSlot = 0);
internal sealed record CashShopPurchaseRequest(byte PaymentMode, IReadOnlyList<CashShopPurchaseItem> Items)
{
    public byte CurrencyMode { get; init; }
    public byte GiftMode { get; init; }
    public byte PcRoom { get; init; }
}
internal sealed record CashShopPurchaseReceipt(
    IReadOnlyList<DfoServer.GameWorld.Items.CashShopProduct> Products,
    IReadOnlyDictionary<int, IReadOnlyList<DfoServer.GameWorld.Items.CashShopBonus>> Bonuses);
