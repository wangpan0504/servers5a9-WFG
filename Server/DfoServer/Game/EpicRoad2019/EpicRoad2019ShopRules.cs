using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.EpicRoad2019;

internal static class EpicRoad2019ShopRules
{
    public static bool Handles(int shopId) => shopId == 400000098;

    public static bool HasMerchant(BuyItemRequest request, TownMap? currentTownMap) =>
        currentTownMap is { Error: null } && currentTownMap.Npcs.Any(npc => npc.Id == request.NpcId);

    public static InventoryEventPrecondition? Prepare(BuyItemRequest request, NpcShopCatalog shops,
        TownMap? currentTownMap, ServerEventSchedule? schedule, TimeProvider clock, bool resourcesAvailable)
    {
        if (!resourcesAvailable || !Handles(request.ShopId) || schedule is null ||
            schedule.EventId != ServerEventService.EpicRoadId || !HasMerchant(request, currentTownMap) ||
            !shops.Allows(request.NpcId, request.ShopId, request.ItemId)) return null;
        schedule.Validate();
        return schedule.IsActive(clock.GetUtcNow()) ? new(schedule, clock) : null;
    }
}
