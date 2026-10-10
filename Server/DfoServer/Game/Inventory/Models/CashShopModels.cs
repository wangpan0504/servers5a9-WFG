namespace DfoServer.Game.Inventory.Models;

internal sealed record CashShopPurchaseItem(int CommodityId, byte AttributeValue);
internal sealed record CashShopPurchaseRequest(byte PaymentMode, IReadOnlyList<CashShopPurchaseItem> Items);
internal sealed record CashShopPurchaseReceipt(
    IReadOnlyList<DfoServer.GameWorld.Items.CashShopProduct> Products,
    IReadOnlyDictionary<int, IReadOnlyList<DfoServer.GameWorld.Items.CashShopBonus>> Bonuses);
