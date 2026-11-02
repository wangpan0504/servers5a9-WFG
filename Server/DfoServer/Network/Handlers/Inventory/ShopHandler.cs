using DfoServer.Game.BattlePass;
using DfoServer.Game.Session;
using System.Data.Common;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Infrastructure;
using DfoServer.Game.Marriage;
using DfoServer.Network.Projection;
using DfoServer.Game.Events;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ShopHandler(InventoryShopService shop, InventoryCashShopService cashShop, InventoryProjection projection,
    ServerLog log, MarriageShopService? marriageShop = null, MarriageProjection? marriageProjection = null, CashShopPromotionService? promotions = null,
    PromotionPageProjection? promotionPage = null, TownProjection? nativePromotionTown = null,
    ServerEventService? serverEvents = null, NpcShopCatalog? strongwayShops = null,
    Func<GameConnectionContext, BattlePassMissionState, InventoryCommitReceipt, Task>? battlePassCertificateChanged = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.BUY_ITEM, Buy, nameof(ShopHandler));
        commands.Register(CmdPacketType.BUY_CERASHOP_ITEM, BuyCeraShop, nameof(ShopHandler));
        commands.Register(CmdPacketType.SELL_ITEM, Sell, nameof(ShopHandler));
        commands.Register(CmdPacketType.SHOP_PURCHASE_COUNT, PurchaseCounts, nameof(ShopHandler));
    }

    async Task<CommandResult> BuyCeraShop(GameConnectionContext context, Packet packet)
    {
        CashShopPurchaseReceipt? receipt = null;
        PromotionPurchaseCommit? promotion = null;
        BattlePassCertificateCommit? certificate = null;
        CashShopPurchaseRequest request;
        InventoryResult result;
        try
        {
            request = CashShopRequests.Buy(packet.Body);
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(17) :
                context.Session.BuyCashShop(request, cashShop, promotions, out receipt, out promotion, out certificate);
        }
        catch (InvalidDataException)
        {
            log.Write("cash_shop_buy", new { Connection = context.Id, Length = packet.Body.Length, Accepted = false, Result = "malformed" });
            await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM, CashShopPackets.Failure(), "cash_shop_buy_malformed");
            return CommandResult.Handled;
        }
        if (promotion is not null)
            log.Write("cash_shop_buy", new { Connection = context.Id, Count = request.Items.Count,
                Order = promotion.OrderId.ToString("N")[..12], Campaign = promotion.Progress.Scope.Campaign,
                promotion.Cost, MailCount = promotion.MailIds.Length, result.Accepted, result.Error,
                Result = "committed", Revision = result.Receipt?.Character.Revision });
        else log.Write("cash_shop_buy", new { Connection = context.Id, Count = request.Items.Count,
            Commodities = string.Join(',', request.Items.Select(item => item.CommodityId)),
            PaymentModes = string.Join(',', request.Items.Select(item => item.PaymentMode)), request.CurrencyMode, request.GiftMode,
            result.Accepted, result.Error, result.RejectReason,
            Result = result.Accepted ? "committed" : "rejected", Revision = result.Receipt?.Character.Revision });
        if (!result.Accepted || receipt is null)
        {
            await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM, CashShopPackets.Failure(), "cash_shop_buy_rejected");
            return CommandResult.Handled;
        }
        var committed = result.Receipt ?? throw new InvalidOperationException("商城购买缺少提交回执");
        if (certificate is not null)
        {
            try { await PublishCertificateAsync(context, result, receipt, committed, certificate); }
            catch (DbException error) { throw new IOException("Committed cash shop purchase publication failed", error); }
            return CommandResult.Handled;
        }
        bool recoverBattlePassPurchase = battlePassCertificateChanged is not null &&
            result.Plan?.CashShopPayment is { EligibleCera: > 0 };
        bool SameOriginLocked() => context.Session.Ready && context.Owner.Generation == committed.Generation &&
            context.Session.SelectionEpoch == committed.SelectionEpoch && context.Session.Phase == committed.Phase &&
            context.Session.Selected?.Id == committed.Character.Id && context.Session.DungeonSnapshot?.Identity == committed.DungeonIdentity;
        bool SameOrigin() => context.Owner.Execute(SameOriginLocked);
        var published = committed;
        try
        {
            foreach (var product in receipt.Products)
                await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM,
                    CashShopPackets.Success(product, receipt.Bonuses.GetValueOrDefault(product.CommodityId)),
                    "cash_shop_buy_success", () => recoverBattlePassPurchase ? SameOrigin() : context.Session.CanPublishInventory(committed));
            await projection.RefreshAsync(context, result, suppressMailboxNotice: promotion is { MailIds.Length: > 0 });
            if (promotion is { MailIds.Length: > 0 } && context.Session.CanPublishInventory(committed))
                context.Session.NotifyMailbox(committed.Character.Id, restoreOnTown: true);
            if (receipt.Products.Any(product => product.IsMileage))
                await context.Notify(NotiPacketType.CASH_SHOP_PURCHASE_LIMIT_INFO,
                    MileagePackets.Limits(committed.Character.Inventory.AccountWallet, cashShop.Mileage), "mileage_shop_limits", () => context.Session.CanPublishInventory(committed));
            if (battlePassCertificateChanged is not null)
            {
                if (result.Plan?.CashShopPayment is { Cera: > 0 } payment &&
                    context.Session.CanPublishInventory(committed) &&
                    context.Session.ReadBattlePassMissionState(payment.Clock.GetUtcNow()) is { State.Active: true } state &&
                    state.Receipt.Character.Id == committed.Character.Id &&
                    state.Receipt.Character.Revision == committed.Character.Revision &&
                    context.Session.CanPublishInventory(committed))
                    await battlePassCertificateChanged(context, state.State, committed);
            }
            if (result.Plan?.UnlockSkillTree == true)
                await projection.RefreshSkillTreeAsync(context, committed.Character, () => context.Session.CanPublishInventory(committed));
            if (recoverBattlePassPurchase && !context.Session.CanPublishInventory(committed))
            {
                var plan = result.Plan!;
                while (true)
                {
                    context.Stop.ThrowIfCancellationRequested();
                    var current = context.Owner.Execute(() =>
                    {
                        if (!SameOriginLocked())
                            return (Receipt: (InventoryCommitReceipt?)null, Mission: (BattlePassMissionSessionState?)null);
                        var mission = context.Session.ReadBattlePassMissionState(plan.CashShopPayment!.Clock.GetUtcNow());
                        return (Receipt: mission?.Receipt ?? context.Session.ReadInventoryForSynchronization(), Mission: mission);
                    });
                    if (current.Receipt is not { } snapshot) break;
                    await projection.ResynchronizeCommittedAsync(context, snapshot, committed, plan);
                    if (plan.Before.PrivilegeService != plan.After.PrivilegeService)
                        await PrivilegeServiceProjection.PostAsync(context, snapshot);
                    if (current.Mission is { State.Active: true } missionState)
                        await battlePassCertificateChanged!(context, missionState.State, snapshot);
                    if (plan.UnlockSkillTree && context.Session.CanPublishInventory(snapshot))
                        await projection.RefreshSkillTreeAsync(context, snapshot.Character, () => context.Session.CanPublishInventory(snapshot));
                    if (receipt.Products.Any(product => product.IsMileage))
                        await context.Notify(NotiPacketType.CASH_SHOP_PURCHASE_LIMIT_INFO,
                            MileagePackets.Limits(snapshot.Character.Inventory.AccountWallet, cashShop.Mileage),
                            "mileage_shop_limits", () => context.Session.CanPublishInventory(snapshot));
                    if (context.Session.CanPublishInventory(snapshot))
                    {
                        if (!plan.OverflowItems.IsEmpty || promotion is { MailIds.Length: > 0 })
                            context.Session.NotifyMailbox(snapshot.Character.Id, restoreOnTown: true);
                        published = snapshot;
                        break;
                    }
                    if (!SameOrigin()) break;
                    await Task.Yield();
                }
            }
            if (promotion is not null && promotionPage is not null)
            {
                string status = promotionPage.Changed(context, promotion.Progress);
                log.Write("cash_shop_promotion_page_changed", new { Connection = context.Id, Status = status, ProgressRevision = promotion.Progress.Revision });
            }
            if (promotion is not null && nativePromotionTown is not null &&
                promotion.Progress.Scope == CashShopPromotionPageService.Scope && context.Session.CanPublishInventory(published))
                await nativePromotionTown.ServerEventsAsync(context, () => context.Session.CanPublishInventory(published),
                    minimumPromotionRevision: promotion.Progress.Revision);
        }
        catch (DbException error) when (promotion is not null || result.Plan?.CashShopPayment is { Cera: > 0 })
        {
            // 联合购买已提交；后续SQL发布失败不能触发外层重放购买。
            throw new IOException("Committed cash shop purchase publication failed", error);
        }
        return CommandResult.Handled;
    }

    async Task PublishCertificateAsync(GameConnectionContext context, InventoryResult result, CashShopPurchaseReceipt purchase,
        InventoryCommitReceipt committed, BattlePassCertificateCommit certificate)
    {
        var plan = result.Plan ?? throw new InvalidOperationException("资格证购买缺少库存计划");
        bool SameOriginLocked() => context.Session.Ready && context.Owner.Generation == committed.Generation &&
            context.Session.SelectionEpoch == committed.SelectionEpoch && context.Session.Phase == committed.Phase &&
            context.Session.Selected?.Id == committed.Character.Id && context.Session.DungeonSnapshot?.Identity == committed.DungeonIdentity;
        bool SameOrigin() => context.Owner.Execute(SameOriginLocked);
        var product = purchase.Products.Single();
        // 订单已提交，ACK属于原订单；后续资产快照另按当前receipt复验。
        await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM,
            CashShopPackets.Success(product, purchase.Bonuses.GetValueOrDefault(product.CommodityId)),
            "cash_shop_buy_success", SameOrigin);
        if (!SameOrigin()) return;

        // 原计划的获得型差量最多发布一次；已经变化的快照直接进入绝对重同步。
        if (context.Session.CanPublishInventory(committed))
        {
            await projection.RefreshAsync(context, result);
            if (context.Session.CanPublishInventory(committed) && battlePassCertificateChanged is not null)
                await battlePassCertificateChanged(context, certificate.State, committed);
            if (plan.UnlockSkillTree && context.Session.CanPublishInventory(committed))
                await projection.RefreshSkillTreeAsync(context, committed.Character, () => context.Session.CanPublishInventory(committed));
            if (context.Session.CanPublishInventory(committed)) return;
        }

        while (true)
        {
            context.Stop.ThrowIfCancellationRequested();
            var current = context.Owner.Execute(() => SameOriginLocked()
                ? context.Session.ReadBattlePassMissionState((plan.CashShopPayment?.Clock ?? TimeProvider.System).GetUtcNow()) : null);
            if (current is null) return;
            await projection.ResynchronizeCommittedAsync(context, current.Receipt, committed, plan);
            if (battlePassCertificateChanged is not null)
                await battlePassCertificateChanged(context, current.State, current.Receipt);
            if (plan.UnlockSkillTree && context.Session.CanPublishInventory(current.Receipt))
                await projection.RefreshSkillTreeAsync(context, current.Receipt.Character,
                    () => context.Session.CanPublishInventory(current.Receipt));
            if (context.Session.CanPublishInventory(current.Receipt))
            {
                if (!plan.OverflowItems.IsEmpty) context.Session.NotifyMailbox(current.Receipt.Character.Id, restoreOnTown: true);
                return;
            }
            if (!SameOrigin()) return;
            await Task.Yield();
        }
    }

    async Task<CommandResult> Buy(GameConnectionContext context, Packet packet)
    {
        var request = ShopRequests.Buy(packet.Body);
        ShopPurchaseReceipt? receipt = null;
        MarriageRecord? marriage = null;
        InventoryResult result;
        if (context.Activity?.BlocksTownActions == true) result = InventoryResult.Reject(17);
        else if (Strongway2019ShopRules.Handles(request.ShopId))
            result = serverEvents is null || strongwayShops is null ? InventoryResult.Reject(17) :
                context.Session.PlanAndCommitStrongway2019ShopBuy(request, shop, strongwayShops, serverEvents, out receipt);
        else if (marriageShop?.Handles(request.ItemId) == true)
        {
            result = InventoryResult.Reject(17);
            if (context.Session.CaptureMarriage(false) is { } capture && context.Session.MarriageStore is { } store &&
                store.FindMarriage(capture.Character.Id) is { } before)
            {
                var planned = marriageShop.Plan(capture.Character, before, request, out var after, out receipt);
                result = planned;
                if (planned.Accepted && after is not null)
                {
                    try
                    {
                        var saved = context.Session.CommitMarriage(capture, planned.Plan!, before, after, requireTown: false);
                        result = saved?.Actor ?? InventoryResult.Reject(17);
                        marriage = saved?.Record;
                    }
                    catch (InvalidOperationException)
                    {
                        // 共享关系的竞争不重放购买，也不能让规划成功越过事务失败。
                        result = InventoryResult.Reject(17);
                        context.Session.RefreshMarriageProjection();
                    }
                }
            }
            log.Write("marriage_shop_buy", new { Connection = context.Id, request.NpcId, request.ShopId, request.ItemId,
                request.Count, result.Accepted, result.Error, Revision = marriage?.Revision,
                Result = result.Accepted ? "committed" : "rejected" });
        }
        else result = context.Session.PlanAndCommitShopBuy(request.ShopId, character => shop.Buy(character, request, out receipt));
        var committed = result.Receipt;
        // ACK先扣客户端材料、再增加获得量；不能在其前面推送已经叠好的最终奖励数量。
        await context.Reply(CmdPacketType.BUY_ITEM, ShopPackets.Bought(result, receipt, committed?.Character.Inventory),
            result.Accepted ? "npc_shop_bought" : "npc_shop_buy_rejected",
            () => committed is null || context.Session.CanPublishInventory(committed));
        await projection.RefreshAsync(context, result);
        if (marriage is not null && marriageProjection is not null)
            await marriageProjection.PublishAsync(context, marriage);
        if (result.Accepted && result.Plan?.NpcMaterialConsume is { } consumed && committed is not null &&
            context.Session.CanPublishInventory(committed))
            await BattlePassMissionProjection.RefreshAsync(context, consumed.Clock.GetUtcNow());
        return CommandResult.Handled;
    }

    async Task<CommandResult> PurchaseCounts(GameConnectionContext context, Packet packet)
    {
        int npc = ShopRequests.PurchaseCounts(packet.Body);
        InventoryCommitReceipt? snapshot = null;
        var rows = context.Activity?.BlocksTownActions == true ? null : context.Session.ReadShopPurchaseCounts(npc, shop, out snapshot);
        await context.Reply(CmdPacketType.SHOP_PURCHASE_COUNT, ShopPackets.PurchaseCounts(rows), "npc_shop_purchase_counts",
            () => snapshot is null || context.Session.CanPublishInventory(snapshot));
        return CommandResult.Handled;
    }

    async Task<CommandResult> Sell(GameConnectionContext context, Packet packet)
    {
        var request = ShopRequests.Sell(packet.Body);
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject() : context.Session.SellAtShop(request, shop);
        var committed = result.Receipt;
        await context.Reply(CmdPacketType.SELL_ITEM, ShopPackets.Sold(result, request), result.Accepted ? "npc_shop_sold" : "npc_shop_sell_rejected",
            () => committed is null || context.Session.CanPublishInventory(committed));
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
