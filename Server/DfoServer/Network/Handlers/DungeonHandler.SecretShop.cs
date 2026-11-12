using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task PublishSecretShopAsync(GameConnectionContext context, bool prepare)
    {
        if (secretShop is null || context.Session.SecretShopPresentation(secretShop, prepare) is not { } view) return;
        bool sent = false;
        await context.Notify(NotiPacketType.SECRET_SHOP_NPC, SecretShopPackets.Merchant(view.Merchant),
            "secret_shop_merchant", () => sent = context.Session.CanPublishFormalDungeon(view.Scene));
        if (sent) context.Session.ConfirmSecretShopPresentation(view.Scene);
        log.Write("secret_shop", new { Connection = context.Id, Action = "appearance", view.Merchant,
            Sent = sent, view.Scene.Entry.DungeonId });
    }
}
