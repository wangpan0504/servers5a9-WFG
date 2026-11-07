using DfoServer.Game.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class AdventureShopHandler(AdventureShopService service, InventoryProjection inventory, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.ADVENTURER_SHOP_PURCHASE, Purchase, nameof(AdventureShopHandler));
    async Task<CommandResult> Purchase(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.Purchase(packet.Body);
        var result = context.Session.BuyAdventure(service, request.Category, request.Item, (clock ?? TimeProvider.System).GetUtcNow());
        bool Guard() => result.Inventory?.Receipt is { } receipt && context.Session.CanPublishInventory(receipt);
        await context.Reply(CmdPacketType.ADVENTURER_SHOP_PURCHASE, AdventurePackets.Ack(result.Error), "adventure_shop_purchase", result.Accepted ? Guard : null);
        if (result.Inventory is { } granted) await inventory.RefreshAsync(context, granted);
        if (result.Accepted && result.Snapshot is { } snapshot && result.Inventory?.Receipt is { } committed)
        {
            await context.Notify(NotiPacketType.ADVENTURE_INFO, AdventurePackets.Information(committed.Character.Id, snapshot, service.Catalog,
                snapshot.Representatives(service.Catalog), context.Channel.ServerIndex), "adventure_shop_information", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(committed.Character.Id, snapshot.State, service.Catalog),
                "adventure_shop_actor", Guard);
        }
        return CommandResult.Handled;
    }
}
