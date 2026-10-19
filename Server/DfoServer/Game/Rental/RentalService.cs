using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Rental;

// 租赁业务只生成普通库存计划；账号钱包、装备实例和费用由现有 InventoryPlan 一次提交。
internal sealed class RentalService(RentalCatalog catalog, ItemCatalog items)
{
    public InventoryResult Charge(Character character, int count)
    {
        var before = character.Inventory;
        RentalChargeValidation validation = RentalRules.ValidateCharge(catalog.PointLimit, before.Gold,
            before.AccountWallet.RentalPoints, catalog.GoldPerPoint, count);
        if (!validation.Accepted || count > catalog.LuckyStarStackLimit)
            return InventoryResult.Reject();
        var after = before with
        {
            Gold = checked((uint)validation.GoldAfter),
            AccountWallet = before.AccountWallet.Set(CurrencyKind.RentalPoint, validation.PointsAfter)
        };
        return new(true, 0, new(before, after, "rental_point_charge", new InventoryCharacterPrecondition(character)));
    }

    public InventoryResult Rent(Character character, int itemId, ushort packedJob, ushort packedTier, DateTimeOffset now)
    {
        if (packedJob != character.Job)
            return InventoryResult.Reject();

        ItemTemplate template;
        int cost;
        try
        {
            template = items.Get(itemId);
            cost = catalog.Cost(character.Job, packedTier, itemId);
        }
        catch (InvalidDataException) { return InventoryResult.Reject(); }
        catch (InvalidOperationException) { return InventoryResult.Reject(); }
        if (template.Kind != ItemKind.Equipment || !template.Expiration.SupportsCreation || template.EquipmentSlot is < 12 or > 24 ||
            (template.Jobs & (1 << character.Job)) == 0)
            return InventoryResult.Reject();

        var before = character.Inventory;
        int points = before.AccountWallet.RentalPoints;
        if (points < cost)
            return InventoryResult.Reject();
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, before.Expansion);
        short slot = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(value => checked((short)value))
            .Where(value => before.Find(new(InventoryListType.Main, value)) is null).DefaultIfEmpty((short)-1).First();
        if (slot < 0)
            return InventoryResult.Reject(17);

        long expires = Math.Min(ItemExpiration.MaximumUnixTime,
            now.ToUnixTimeSeconds() + TimeSpan.FromHours(24).Ticks / TimeSpan.TicksPerSecond);
        ItemCore core = InventoryRewardGrantService.Create(template, utcNow: now) with
        {
            ExpireTime = checked((int)expires),
            IsRental = true
        };
        var item = new InventoryItem(0, new(InventoryListType.Main, slot), core);
        var after = before with
        {
            Items = before.Items.Add(item).OrderBy(value => value.Position.List).ThenBy(value => value.Position.Index).ToImmutableArray(),
            AccountWallet = before.AccountWallet.Set(CurrencyKind.RentalPoint, points - cost)
        };
        after.Validate(allocating: true);
        return new(true, 0, new(before, after, "rental_equipment", new InventoryCharacterPrecondition(character)),
            MoveCount: 1, MoveSource: item.Position);
    }
}
