using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class DaybreakEntryCost
{
    internal const int PassItemId = 490011739;
    internal const int DailyAttendancePassItemId = 490013045;

    // 两款券只抵扣黎明的20个诞生之芽，不增加次数；保留疲劳商店券优先的兼容选择。
    public static DungeonRequiredItem? Select(int dungeonId, DungeonRequiredItem? required, InventorySnapshot inventory,
        EpicPieceCatalog? pieces, DateTimeOffset utcNow)
    {
        if (dungeonId != 530 || required is not { ItemId: 10161610, Count: 20, Flag: 1 } || pieces is null)
            return required;
        if (InventoryMaterialConsumptionService.Plan(inventory, [new(PassItemId, 1)], pieces, utcNow).Accepted)
            return new(PassItemId, 1, 1);
        return InventoryMaterialConsumptionService.Plan(inventory, [new(DailyAttendancePassItemId, 1)], pieces, utcNow).Accepted
            ? new(DailyAttendancePassItemId, 1, 1) : required;
    }
}
