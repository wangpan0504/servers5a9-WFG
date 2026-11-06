namespace DfoServer.Game.Inventory.Models;

internal sealed record CashShopPurchaseItem(int CommodityId, byte AttributeValue, byte TypeIndex = 0, byte PaymentMode = 0,
    byte FirstOptionCount = 0, byte SecondOptionCount = 0, uint RelatedItemId = 0, ushort RelatedSlot = 0)
{
    public IReadOnlyList<CeraPackageChoice> AvatarChoices { get; init; } = [];
    public IReadOnlyList<CashShopPackageChoice> PackageChoices { get; init; } = [];
}
// 原生第二段为礼包源编号、分组序号、组内选项序号，不是奖励编号和数量。
internal sealed record CashShopPackageChoice(int PackageItemId, ushort GroupIndex, ushort SelectionIndex);
internal sealed record CashShopPurchaseRequest(byte PaymentMode, IReadOnlyList<CashShopPurchaseItem> Items)
{
    public byte CurrencyMode { get; init; }
    public byte GiftMode { get; init; }
    public byte PcRoom { get; init; }
}
internal sealed record CashShopPurchaseReceipt(
    IReadOnlyList<DfoServer.GameWorld.Items.CashShopProduct> Products,
    IReadOnlyDictionary<int, IReadOnlyList<DfoServer.GameWorld.Items.CashShopBonus>> Bonuses);
