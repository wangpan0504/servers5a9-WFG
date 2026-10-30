using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.CashShopPromotions;

internal sealed record PromotionOrder(Guid Id, string Account, ushort RecipientId,
    PromotionScope Scope, long ExpectedCount, long ExpectedRevision,
    int Units, DateTimeOffset PlannedAt);
internal sealed record PromotionPurchasePlan(PromotionOrder Order, Character Expected,
    InventoryPlan Inventory, CashShopPurchaseReceipt Purchase,
    ImmutableArray<PromotionAward> Awards, ImmutableArray<MailboxDelivery> Deliveries)
{
    public required PromotionRules Rules { get; init; }
    public PromotionPolicy? Policy { get; init; }
    public PromotionPolicy EffectivePolicy => Policy ?? PromotionPolicy.Pilot(Rules);
    public required long CatalogRevision { get; init; }
}
internal sealed record PromotionPurchaseCommit(Character Character, Guid OrderId,
    PromotionProgress Progress, ImmutableArray<uint> MailIds)
{
    // 本次在线用例的有界标量诊断，不进入订单身份或持久化事实。
    public PromotionPurchaseCost? Cost { get; init; }
}
internal sealed record PromotionPurchaseCost(double CaptureMilliseconds, double PlanMilliseconds,
    double CommitMilliseconds, long AllocatedBytes, int Units, int OverflowInstances, int OverflowMails);
internal interface ICashShopPromotionStore
{
    PromotionProgress Read(AccountOwners.Lease owner, PromotionScope scope);
    PromotionProgress Read(AccountOwners.Lease owner, PromotionCatalogEntry entry);
    PromotionPurchaseCommit? RestoreCommitted(AccountOwners.Lease owner, Guid orderId);
    PromotionPurchaseCommit Commit(AccountOwners.Lease owner, PromotionPurchasePlan plan, DateTimeOffset now);
}

internal static class PromotionMailKey
{
    public static string Create(PromotionOrder order, long firstPurchase, int part)
    {
        string identity = System.Text.Json.JsonSerializer.Serialize(new { order.Account, order.Scope });
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return $"promo:{digest}:{order.Id:N}:{firstPurchase}:{part}";
    }
}
