using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class SecretShopHandler(SecretShopService service, InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SECRET_SHOP_OPEN_CLOSE, Open, nameof(SecretShopHandler));
        commands.Register(CmdPacketType.SECRET_SHOP_BUY_ITEM, Buy, nameof(SecretShopHandler));
    }

    async Task<CommandResult> Open(GameConnectionContext context, Packet packet)
    {
        bool open;
        try { open = SecretShopRequests.Open(packet.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        var view = context.Session.OpenSecretShop(service, open);
        log.Write("secret_shop", new { Connection = context.Id, Action = open ? "open" : "close", Accepted = view is not null });
        if (open && view is { } current)
            await context.Notify(NotiPacketType.SECRET_SHOP_ITEM_LIST, SecretShopPackets.Offers(current.State),
                "secret_shop_offers", () => context.Session.CanPublishFormalDungeon(current.Scene));
        // 297无本版ACK注册，打开由280装载列表；关闭只释放当前实例的开店状态。
        return CommandResult.Handled;
    }

    async Task<CommandResult> Buy(GameConnectionContext context, Packet packet)
    {
        SecretShopPurchase request;
        try { request = SecretShopRequests.Buy(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.SECRET_SHOP_BUY_ITEM,
                CargoPackets.Ack(InventoryResult.Reject(17)), "secret_shop_malformed");
            return CommandResult.Handled;
        }
        var operation = context.Session.BuySecretShop(service, request);
        bool Guard() => operation.Scene is { } scene && context.Session.CanPublishFormalDungeon(scene);
        log.Write("secret_shop", new { Connection = context.Id, Action = "buy", request.ItemId, request.Count,
            operation.Result.Accepted, operation.Result.Error, operation.Result.RejectReason,
            Revision = operation.Result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.SECRET_SHOP_BUY_ITEM,
            SecretShopPackets.Bought(operation.Result, operation.Purchase), "secret_shop_buy_ack",
            operation.Scene is null ? null : Guard);
        if (operation.Result.Accepted) await projection.RefreshAsync(context, operation.Result);
        return CommandResult.Handled;
    }
}
