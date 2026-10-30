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

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ShopHandler(InventoryShopService shop, InventoryCashShopService cashShop, InventoryProjection projection,
    ServerLog log, MarriageShopService? marriageShop = null, MarriageProjection? marriageProjection = null, CashShopPromotionService? promotions = null,
    PromotionPageProjection? promotionPage = null, TownProjection? nativePromotionTown = null) : IGameCommandModule
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
        CashShopPurchaseRequest request;
        InventoryResult result;
        try
        {
            request = CashShopRequests.Buy(packet.Body);
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(17) :
                context.Session.BuyCashShop(request, cashShop, promotions, out receipt, out promotion);
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
        try
        {
            foreach (var product in receipt.Products)
                await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM,
                    CashShopPackets.Success(product, receipt.Bonuses.GetValueOrDefault(product.CommodityId)),
                    "cash_shop_buy_success", () => context.Session.CanPublishInventory(committed));
            await projection.RefreshAsync(context, result, suppressMailboxNotice: promotion is { MailIds.Length: > 0 });
            if (promotion is { MailIds.Length: > 0 } && context.Session.CanPublishInventory(committed))
                context.Session.NotifyMailbox(committed.Character.Id, restoreOnTown: true);
            if (receipt.Products.Any(product => product.IsMileage))
                await context.Notify(NotiPacketType.CASH_SHOP_PURCHASE_LIMIT_INFO,
                    MileagePackets.Limits(committed.Character.Inventory.AccountWallet, cashShop.Mileage), "mileage_shop_limits", () => context.Session.CanPublishInventory(committed));
            if (result.Plan?.UnlockSkillTree == true)
                await projection.RefreshSkillTreeAsync(context, committed.Character, () => context.Session.CanPublishInventory(committed));
            if (promotion is not null && promotionPage is not null)
            {
                string status = promotionPage.Changed(context, promotion.Progress);
                log.Write("cash_shop_promotion_page_changed", new { Connection = context.Id, Status = status, ProgressRevision = promotion.Progress.Revision });
            }
            if (promotion is not null && nativePromotionTown is not null &&
                promotion.Progress.Scope == CashShopPromotionPageService.Scope && context.Session.CanPublishInventory(committed))
                await nativePromotionTown.ServerEventsAsync(context, () => context.Session.CanPublishInventory(committed),
                    minimumPromotionRevision: promotion.Progress.Revision);
        }
        catch (DbException error) when (promotion is not null)
        {
            // 促销已提交；后续SQL发布失败不能触发外层重放购买。
            throw new IOException("Committed promotion purchase publication failed", error);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> Buy(GameConnectionContext context, Packet packet)
    {
        var request = ShopRequests.Buy(packet.Body);
        ShopPurchaseReceipt? receipt = null;
        MarriageRecord? marriage = null;
        InventoryResult result;
        if (context.Activity?.BlocksTownActions == true) result = InventoryResult.Reject(17);
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
