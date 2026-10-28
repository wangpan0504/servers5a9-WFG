using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class DimensionEntryTicketService(ItemCatalog items, TimeProvider? timeProvider = null)
{
    // 全部追加券共用正式五次券的日账本键；Used记录追加次数，不是消耗券数。
    public const int SharedCounterItem = 10161066;
    public const uint Action = 159;
    public TimeProvider Clock { get; } = timeProvider ?? TimeProvider.System;

    public static int Extra(InventorySnapshot inventory, DateTimeOffset now) =>
        DailyBoosterUsage.Effective(inventory.DailyBoosterUses, SharedCounterItem, ShopPurchaseCount.Today(now));

    public InventoryResult Plan(Character character, InventorySlot position, int limit, string place)
    {
        var source = character.Inventory.Find(position);
        if (source is null || items.ActionConsumable(source.Core.ItemId) is not
            { Label: "[add dimension dungeon enter count]", Parameters: [> 0 and <= 5] } definition ||
            Extra(character.Inventory, Clock.GetUtcNow()) + definition.Parameters[0] > limit)
            return InventoryResult.Reject(19);
        var now = Clock.GetUtcNow();
        var paid = InventoryActionConsumableService.Plan(character, position, definition, place, Clock, now);
        if (!paid.Accepted || !DailyBoosterUsage.TryConsume(paid.Plan!.After.DailyBoosterUses, SharedCounterItem,
            definition.Parameters[0], checked((byte)limit), now, out var uses)) return InventoryResult.Reject(19);
        var deadline = Math.Min(source.ExpireTime > 0 ? source.ExpireTime : long.MaxValue,
            DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        return new(true, 0, new(character.Inventory, paid.Plan.After with { DailyBoosterUses = uses }, "dimension_entry_ticket",
            new(character, deadline, Clock, requirePosition: true)));
    }
}
