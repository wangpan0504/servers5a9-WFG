using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassCertificatePurchase
{
    public const int Commodity = 105659, Item = 490705043, Cera = 4900;
    public static bool Contains(CashShopPurchaseRequest request) => request.Items.Any(row => row.CommodityId == Commodity);
    public static bool IsRequest(CashShopPurchaseRequest request) => request is { CurrencyMode: 0, GiftMode: 0, PaymentMode: 0 } &&
        request.Items is [{ CommodityId: Commodity, PaymentMode: 0, FirstOptionCount: 0, SecondOptionCount: 0,
            RelatedItemId: 0, RelatedSlot: 0 }];
}

internal sealed record BattlePassCertificatePlan(BattlePassSeasonState Before, InventoryPlan Inventory,
    CashShopPurchaseRequest Request, CashShopPurchaseReceipt Receipt, Guid OrderId, DateTimeOffset PlannedAt, TimeProvider Clock);
internal sealed record BattlePassCertificateCommit(Character Character, BattlePassMissionState State, Guid OrderId);
internal sealed class BattlePassCertificateChangedException() : InvalidOperationException("战令资格或购买前提已变化");

internal interface IBattlePassCertificateStore : IBattlePassMissionStore
{
    BattlePassCertificateCommit CommitBattlePassCertificate(AccountOwners.Lease owner, Character expected,
        BattlePassCertificatePlan plan, DateTimeOffset now);
}
