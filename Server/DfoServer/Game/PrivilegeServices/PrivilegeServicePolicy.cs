using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.PrivilegeServices;

internal static class PrivilegeServicePolicy
{
    public static PrivilegeServiceState Purchase(PrivilegeServiceState before, CashShopProduct product,
        PrivilegeServiceCatalog catalog, DateTimeOffset now)
    {
        before.Validate();
        long seconds = now.ToUnixTimeSeconds();
        if (before.ExpiresAt > seconds || product.Section != "privilege service" || product.Count != 1 ||
            product.DurationDays <= 0 || !catalog.PurchaseItems.Contains(product.ItemId))
            throw new InvalidOperationException("礼遇特权未到期或购买声明无效");
        var state = new PrivilegeServiceState(seconds, checked(seconds + (long)product.DurationDays * 86400),
            catalog.CurrentSeason, catalog.CurrentSeason, Day(now), 1, 0, 0);
        state.Validate();
        return state;
    }

    public static InventoryResult Login(Character character, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var before = character.Inventory;
        var state = before.PrivilegeService.ObserveLogin(now.ToUnixTimeSeconds(), Day(now));
        return new(true, 0, new(before, before with { PrivilegeService = state }, "privilege_login",
            new(character, state.IsActive(now.ToUnixTimeSeconds()) ? state.ExpiresAt : null, clock)));
    }

    public static int Day(DateTimeOffset now) => FatigueDailyReset.Day(now, TimeZoneInfo.Local);

}
