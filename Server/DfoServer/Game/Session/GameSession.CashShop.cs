using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using System.Diagnostics;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult BuyCashShop(CashShopPurchaseRequest request, InventoryCashShopService cashShop,
        CashShopPromotionService? promotions, out CashShopPurchaseReceipt? receipt, out PromotionPurchaseCommit? committed)
    {
        receipt = null;
        committed = null;
        CashShopPurchaseReceipt? purchase = null;
        var catalog = promotions?.Current;
        PromotionCatalogEntry? entry;
        try { entry = catalog is null ? null : CashShopPromotionService.Select(catalog, request); }
        catch (InvalidDataException) { return InventoryResult.Reject(17); }
        bool CatalogIsCurrent() => catalog is null || promotions!.Current.Revision == catalog.Revision;
        if (entry is null)
        {
            var ordinary = PlanAndCommitInventory(character => cashShop.Buy(character, request, out purchase), 17,
                requireTown: false, allowSettlement: false, additionalGuard: CatalogIsCurrent);
            receipt = ordinary.Accepted ? purchase : null;
            return ordinary;
        }
        if (promotionStore is null || request.CurrencyMode != 0 || request.GiftMode != 0 ||
            request.PaymentMode != 0 || request.Items.Any(row => row.PaymentMode != 0))
            return InventoryResult.Reject(17);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        var captured = owner.Execute(() =>
        {
            var state = CaptureInventoryPlan(requireTown: false, additionalGuard: CatalogIsCurrent);
            try { return (State: state, Progress: state is null ? null : promotionStore.Read(owner, entry)); }
            catch (InvalidOperationException) { return (State: state, Progress: (PromotionProgress?)null); }
        });
        if (captured.State is not { } capture || captured.Progress is null)
            return InventoryResult.Reject(17);
        double captureMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long planningStarted = Stopwatch.GetTimestamp();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var result = cashShop.Buy(capture.Character, request, out purchase, now);
        if (!result.Accepted || result.Plan is null || purchase is null) return result;
        PromotionPurchasePlan plan;
        try
        {
            plan = promotions!.Plan(capture.Character, result.Plan, purchase, captured.Progress, entry,
                catalog!.Revision, owner.Account, now);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return InventoryResult.Reject(17); }
        double planMilliseconds = Stopwatch.GetElapsedTime(planningStarted).TotalMilliseconds;
        double commitMilliseconds = 0;
        PromotionPurchaseCommit? saved = null;
        var final = owner.Execute(() =>
        {
            long commitStarted = Stopwatch.GetTimestamp();
            if (!InventoryPlanIsCurrent(capture, requireTown: false, additionalGuard: CatalogIsCurrent))
                return InventoryResult.Reject(17);
            try { saved = promotionStore.Commit(owner, plan, (timeProvider ?? TimeProvider.System).GetUtcNow()); }
            catch (Exception error) when (error is InvalidOperationException or OverflowException) { return InventoryResult.Reject(17); }
            Selected = saved.Character;
            ObservePromotionPageCommit(catalog!, saved.Progress);
            if (!saved.MailIds.IsEmpty || !result.Plan.OverflowItems.IsEmpty) MarkMailboxRestore();
            commitMilliseconds = Stopwatch.GetElapsedTime(commitStarted).TotalMilliseconds;
            return result with { Receipt = InventoryReceipt(capture.Character) };
        });
        receipt = final.Accepted ? purchase : null;
        committed = saved is null ? null : saved with { Cost = new(captureMilliseconds, planMilliseconds, commitMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocated, plan.Order.Units, result.Plan.OverflowItems.Length,
            (result.Plan.OverflowItems.Length + 9) / 10) };
        return final;
    }
}
