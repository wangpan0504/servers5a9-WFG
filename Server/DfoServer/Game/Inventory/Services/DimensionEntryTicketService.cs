using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class DimensionEntryTicketService(ItemCatalog items, TimeProvider? timeProvider = null)
{
    // 全部追加券共用正式五次券的日账本键；Used记录追加次数，不是消耗券数。
    public const int SharedCounterItem = 10161066;
    public const uint Action = 159;
    // 追加券不再受 PVF 的 5 次上限约束；账本按 byte 存储，单日最多 255 次。
    public const int UnlimitedExtraEntries = byte.MaxValue;
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
        // 七种异界追加券共用同一天账本；不沿用单物品自身的每日1次限制，否则同种券第二张会被误拒。
        var paid = InventoryActionConsumableService.Plan(character, position, definition with { DailyLimit = 0, CoolTime = 0 }, place, Clock, now, ignoreCooldown: true);
        if (!paid.Accepted || !DailyBoosterUsage.TryConsume(paid.Plan!.After.DailyBoosterUses, SharedCounterItem,
            definition.Parameters[0], checked((byte)limit), now, out var uses)) return InventoryResult.Reject(19);
        var deadline = Math.Min(source.ExpireTime > 0 ? source.ExpireTime : long.MaxValue,
            DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        return new(true, 0, new(character.Inventory, paid.Plan.After with { DailyBoosterUses = uses }, "dimension_entry_ticket",
            new(character, deadline, Clock, requirePosition: true)));
    }
}
