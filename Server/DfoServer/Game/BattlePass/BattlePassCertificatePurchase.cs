using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassCertificatePurchase
{
    public const int Commodity = 105659, Item = 490705043, Cera = 4900;
    public static bool Contains(CashShopPurchaseRequest request) => request.Items.Any(row => row.CommodityId == Commodity);
    public static bool IsRequest(CashShopPurchaseRequest request) => request is { CurrencyMode: 0, GiftMode: 0, PaymentMode: 0 } &&
        request.Items is [{ CommodityId: Commodity, PaymentMode: 0, FirstOptionCount: 0, SecondOptionCount: 0,
            RelatedItemId: 0, RelatedSlot: 0 }];
}

internal static class BattlePassDeluxePurchase
{
    public const int Commodity = 105658, Item = 490705044, TargetLevel = 40;

    public static CashShopProduct? Resolve(CashShopCatalog catalog) =>
        catalog.Product(Commodity) is { ItemId: Item, Count: 1, CeraPrice: > 0, Section: "package",
            GoldPrice: 0, OtherPrice: 0, IsMileage: false } offer && catalog.Mileage.CeraOnly.Contains(Item)
            ? offer : null;

    public static bool Contains(CashShopPurchaseRequest request, CashShopProduct? offer) =>
        offer is not null && request.Items.Any(row => row.CommodityId == offer.CommodityId);

    public static bool IsRequest(CashShopPurchaseRequest request, CashShopProduct? offer) =>
        offer is not null && request is { CurrencyMode: 0, GiftMode: 0, PaymentMode: 0 } &&
        request.Items is [{ CommodityId: var commodity, PaymentMode: 0, FirstOptionCount: 0, SecondOptionCount: 0,
            RelatedItemId: 0, RelatedSlot: 0 }] && commodity == offer.CommodityId;
}

internal sealed record BattlePassCertificatePlan(BattlePassSeasonState Before, InventoryPlan Inventory,
    CashShopPurchaseRequest Request, CashShopPurchaseReceipt Receipt, Guid OrderId, DateTimeOffset PlannedAt, TimeProvider Clock,
    CashShopProduct? DeluxeOffer = null);
internal sealed record BattlePassCertificateCommit(Character Character, BattlePassMissionState State, Guid OrderId);
internal sealed class BattlePassCertificateChangedException() : InvalidOperationException("战令资格或购买前提已变化");

internal interface IBattlePassCertificateStore : IBattlePassMissionStore
{
    BattlePassCertificateCommit CommitBattlePassCertificate(AccountOwners.Lease owner, Character expected,
        BattlePassCertificatePlan plan, DateTimeOffset now);
}
