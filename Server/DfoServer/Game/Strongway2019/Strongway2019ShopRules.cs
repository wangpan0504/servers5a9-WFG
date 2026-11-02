using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Strongway2019;

internal static class Strongway2019ShopRules
{
    public static bool Handles(int shopId) => shopId is 400000084 or 400000086;

    // 入口051由312通过639动态呈现，未写入PVF静态地图名册；只补同一38/1入口商人。
    public static bool HasMerchant(BuyItemRequest request, TownMap? currentTownMap, Location? currentPosition = null) =>
        currentTownMap is { Error: null } &&
        (currentTownMap.Npcs.Any(npc => npc.Id == request.NpcId) ||
         currentPosition is { Town: 38, Area: 1 } && request is { NpcId: 400001051, ShopId: 400000084 });

    // currentTownMap由Session按当前Town阶段、位置和任务解析；非Town传null。
    // 312只作服务端玩法时段控制，不表示启用遗留活动窗或其领奖协议。
    public static InventoryEventPrecondition? Prepare(BuyItemRequest request, NpcShopCatalog shops,
        TownMap? currentTownMap, ServerEventSchedule? schedule, TimeProvider clock, Location? currentPosition = null)
    {
        if (!Handles(request.ShopId) || schedule is null || schedule.EventId != ServerEventService.Strongway2019Id ||
            !HasMerchant(request, currentTownMap, currentPosition) ||
            !shops.Allows(request.NpcId, request.ShopId, request.ItemId)) return null;
        schedule.Validate();
        return schedule.IsActive(clock.GetUtcNow()) ? new(schedule, clock) : null;
    }
}
