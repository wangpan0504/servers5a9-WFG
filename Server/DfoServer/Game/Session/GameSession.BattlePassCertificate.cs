using System.Collections.Immutable;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    InventoryResult BuyBattlePassCertificate(CashShopPurchaseRequest request, InventoryCashShopService cashShop,
        out CashShopPurchaseReceipt? receipt, out BattlePassCertificateCommit? committed)
    {
        receipt = null; committed = null;
        if (!BattlePassCertificatePurchase.IsRequest(request) || repository is not IBattlePassCertificateStore store)
            return InventoryResult.Reject(17);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var captured = owner.Execute(() =>
        {
            var capture = CaptureInventoryPlan(requireTown: false);
            return (Capture: capture, State: capture is null ? null : store.ReadBattlePassMissions(owner, now));
        });
        if (captured.Capture is not { } capture || captured.State is not { Active: true, Season.PassType: 0 } state)
            return InventoryResult.Reject(17) with { RejectReason = "battle_pass_certificate_ineligible" };
        var result = cashShop.BuyBattlePassCertificate(capture.Character, request, out var purchase, now);
        if (!result.Accepted || result.Plan is null || purchase is null) return result;
        // 请求列表可能由调用方持有；计划保存独立不可变业务快照，客户端未知字段不充当订单号。
        var plan = new BattlePassCertificatePlan(state.Season, result.Plan,
            request with { Items = request.Items.ToImmutableArray() }, purchase, Guid.NewGuid(), now, timeProvider ?? TimeProvider.System);
        BattlePassCertificateCommit? saved = null;
        var final = owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false)) return InventoryResult.Reject(17);
            try { saved = store.CommitBattlePassCertificate(owner, capture.Character, plan, (timeProvider ?? TimeProvider.System).GetUtcNow()); }
            catch (BattlePassCertificateChangedException) { return InventoryResult.Reject(17) with { RejectReason = "battle_pass_certificate_changed" }; }
            catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(17) with { RejectReason = "qualification_expired" }; }
            catch (InventoryEventRejectedException) { return InventoryResult.Reject(17) with { RejectReason = "event_changed" }; }
            catch (OverflowException) { return InventoryResult.Reject(17) with { RejectReason = "battle_pass_certificate_overflow" }; }
            catch (InvalidOperationException) { return InventoryResult.Reject(17) with { RejectReason = "inventory_changed" }; }
            Selected = saved.Character;
            if (!result.Plan.OverflowItems.IsEmpty) MarkMailboxRestore();
            return result with { Receipt = InventoryReceipt(capture.Character) };
        });
        receipt = final.Accepted ? purchase : null;
        committed = final.Accepted ? saved : null;
        return final;
    }

    InventoryResult BuyBattlePassDeluxe(CashShopPurchaseRequest request, InventoryCashShopService cashShop,
        out CashShopPurchaseReceipt? receipt, out BattlePassCertificateCommit? committed)
    {
        receipt = null; committed = null;
        if (!BattlePassDeluxePurchase.IsRequest(request, cashShop.BattlePassDeluxeOffer) || repository is not IBattlePassCertificateStore store)
            return InventoryResult.Reject(17);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var captured = owner.Execute(() =>
        {
            var capture = CaptureInventoryPlan(requireTown: false);
            return (Capture: capture, State: capture is null ? null : store.ReadBattlePassMissions(owner, now));
        });
        if (captured.Capture is not { } capture || captured.State is not { Active: true, Season.PassType: 0 or 1 } state)
            return InventoryResult.Reject(17) with { RejectReason = "battle_pass_deluxe_ineligible" };
        var result = cashShop.BuyBattlePassDeluxe(capture.Character, request, out var purchase, now);
        if (!result.Accepted || result.Plan is null || purchase is null) return result;
        var plan = new BattlePassCertificatePlan(state.Season, result.Plan,
            request with { Items = request.Items.ToImmutableArray() }, purchase, Guid.NewGuid(), now,
            timeProvider ?? TimeProvider.System, cashShop.BattlePassDeluxeOffer);
        BattlePassCertificateCommit? saved = null;
        var final = owner.Execute(() =>
        {
            if (!InventoryPlanIsCurrent(capture, requireTown: false)) return InventoryResult.Reject(17);
            try { saved = store.CommitBattlePassCertificate(owner, capture.Character, plan, (timeProvider ?? TimeProvider.System).GetUtcNow()); }
            catch (BattlePassCertificateChangedException) { return InventoryResult.Reject(17) with { RejectReason = "battle_pass_deluxe_changed" }; }
            catch (InventoryQualificationExpiredException) { return InventoryResult.Reject(17) with { RejectReason = "qualification_expired" }; }
            catch (InventoryEventRejectedException) { return InventoryResult.Reject(17) with { RejectReason = "event_changed" }; }
            catch (OverflowException) { return InventoryResult.Reject(17) with { RejectReason = "battle_pass_deluxe_overflow" }; }
            catch (InvalidOperationException) { return InventoryResult.Reject(17) with { RejectReason = "inventory_changed" }; }
            Selected = saved.Character;
            if (!result.Plan.OverflowItems.IsEmpty) MarkMailboxRestore();
            return result with { Receipt = InventoryReceipt(capture.Character) };
        });
        receipt = final.Accepted ? purchase : null;
        committed = final.Accepted ? saved : null;
        return final;
    }
}
