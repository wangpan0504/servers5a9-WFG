using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class DaybreakEntryCost
{
    internal const int PassItemId = 490011739;

    // 疲劳商店券只抵扣黎明的20个诞生之芽，不增加次数；锁、期限与容器沿统一材料规划。
    public static DungeonRequiredItem? Select(int dungeonId, DungeonRequiredItem? required, InventorySnapshot inventory,
        EpicPieceCatalog? pieces, DateTimeOffset utcNow) =>
        dungeonId == 530 && required is { ItemId: 10161610, Count: 20, Flag: 1 } && pieces is not null &&
        InventoryMaterialConsumptionService.Plan(inventory, [new(PassItemId, 1)], pieces, utcNow).Accepted
            ? new(PassItemId, 1, 1) : required;
}
