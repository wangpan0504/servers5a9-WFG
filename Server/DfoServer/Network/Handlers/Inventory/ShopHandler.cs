using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ShopHandler(InventoryShopService shop, InventoryCashShopService cashShop, InventoryProjection projection) : IGameCommandModule
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
        InventoryResult result;
        try
        {
            var request = CashShopRequests.Buy(packet.Body);
            result = context.BlocksTownActions ? InventoryResult.Reject(17) :
                context.Session.MutateInventory(character => cashShop.Buy(character, request, out receipt), 17);
        }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM, CashShopPackets.Failure(), "cash_shop_buy_malformed");
            return CommandResult.Handled;
        }
        if (!result.Accepted || receipt is null)
        {
            await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM, CashShopPackets.Failure(), "cash_shop_buy_rejected");
            return CommandResult.Handled;
        }
        foreach (var product in receipt.Products)
            await context.Reply(CmdPacketType.BUY_CERASHOP_ITEM,
                CashShopPackets.Success(product, receipt.Bonuses.GetValueOrDefault(product.CommodityId)),
                "cash_shop_buy_success");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Buy(GameConnectionContext context, Packet packet)
    {
        var request = ShopRequests.Buy(packet.Body);
        ShopPurchaseReceipt? receipt = null;
        var result = context.BlocksTownActions ? InventoryResult.Reject(17) :
            context.Session.MutateInventory(character => shop.Buy(character, request, out receipt), 17);
        // ACK先扣客户端材料、再增加获得量；不能在其前面推送已经叠好的最终奖励数量。
        await context.Reply(CmdPacketType.BUY_ITEM, ShopPackets.Bought(result, receipt, context.Session.Selected?.Inventory), result.Accepted ? "npc_shop_bought" : "npc_shop_buy_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PurchaseCounts(GameConnectionContext context, Packet packet)
    {
        int npc = ShopRequests.PurchaseCounts(packet.Body);
        var rows = context.Owner.Execute(() => context.BlocksTownActions || !context.Session.Ready ||
            context.Session.Phase != Game.Session.SessionPhase.Town || context.Session.Selected is not { } character ?
            null : shop.PurchaseCounts(character, npc));
        await context.Reply(CmdPacketType.SHOP_PURCHASE_COUNT, ShopPackets.PurchaseCounts(rows), "npc_shop_purchase_counts");
        return CommandResult.Handled;
    }

    async Task<CommandResult> Sell(GameConnectionContext context, Packet packet)
    {
        var request = ShopRequests.Sell(packet.Body);
        var result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject() : context.Session.SellAtShop(request, shop);
        await context.Reply(CmdPacketType.SELL_ITEM, ShopPackets.Sold(result, request), result.Accepted ? "npc_shop_sold" : "npc_shop_sell_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
