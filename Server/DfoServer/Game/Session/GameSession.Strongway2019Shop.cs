using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult PlanAndCommitStrongway2019ShopBuy(BuyItemRequest request, InventoryShopService shop,
        NpcShopCatalog shops, ServerEventService events, out ShopPurchaseReceipt? receipt)
    {
        ShopPurchaseReceipt? plannedReceipt = null;
        var result = PlanAndCommitInventory(character =>
        {
            var required = Strongway2019ShopRules.Prepare(request, shops, MerchantMap(character),
                events.Read(ServerEventService.Strongway2019Id).Schedule, shop.Clock, character.Position);
            return required is null ? InventoryResult.Reject(17) :
                shop.Buy(character, request, out plannedReceipt, requiredEvent: required);
        }, 17, requireTown: true, allowSettlement: false, additionalGuard: () =>
            Strongway2019ShopRules.Handles(request.ShopId) && Selected is { } character &&
            Strongway2019ShopRules.HasMerchant(request, MerchantMap(character), character.Position));
        receipt = result.Accepted ? plannedReceipt : null;
        return result;

        TownMap? MerchantMap(Character character) => towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area)
            ? area.Map(QuestEligibility.Completed(character)) : null;
    }
}
