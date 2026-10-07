using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryCargoService(ItemCatalog catalog, CargoCatalog cargo, GoldLimitCatalog goldLimits)
{
    public static bool AllowsInsert(ItemCore core, InventoryListType list, ItemTemplate template)
    {
        string attach = template.AttachType.Replace("`", "").Trim().Trim('[', ']').Trim();
        return list switch
        {
            InventoryListType.PersonalCargo => !template.PersonalCargoForbidden && !attach.Equals("trade delete", StringComparison.OrdinalIgnoreCase),
            InventoryListType.AccountCargo => !template.AccountCargoForbidden && core.EquipmentLockId == 0 && core.TradeRestriction != 1 &&
                (attach.Equals("free", StringComparison.OrdinalIgnoreCase) || attach.Equals("account", StringComparison.OrdinalIgnoreCase) ||
                 attach.Equals("sealing", StringComparison.OrdinalIgnoreCase)),
            _ => true
        };
    }

    public InventoryResult AccountUpgrade(Character character, bool create)
    {
        var before = character.Inventory;
        if (create != (before.AccountCargo.Capacity == 0))
            return InventoryResult.Reject(create ? (ushort)20 : (ushort)21);
        if (create && character.Level < cargo.RequiredLevel)
            return InventoryResult.Reject(14);
        var tier = cargo.Next(before.AccountCargo.Capacity);
        if (tier is null)
            return InventoryResult.Reject(19);
        if (before.Gold < tier.GoldCost || before.AccountWallet.Get(CurrencyKind.Cera) < tier.CeraCost)
            return InventoryResult.Reject(20);
        var after = before with
        {
            Gold = checked(before.Gold - (uint)tier.GoldCost),
            AccountWallet = before.AccountWallet.Set(CurrencyKind.Cera, before.AccountWallet.Get(CurrencyKind.Cera) - tier.CeraCost)
        };
        int remaining = tier.MaterialCount;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in before.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == tier.MaterialId &&
                     item.Core.IsStackable && item.Core.EquipmentLockId == 0 && !item.Core.IsExpired(now)).OrderBy(item => item.Position.Index))
        {
            int consumed = Math.Min(remaining, item.Core.Count);
            if (consumed == 0)
                break;
            var deletion = InventoryDeleteService.Plan(after, item.Position, item.Core.Value, consumed);
            if (!deletion.Accepted)
                return InventoryResult.Reject(20);
            after = deletion.Plan!.After;
            remaining -= consumed;
        }
        if (remaining != 0)
            return InventoryResult.Reject(20);
        after = after with { AccountCargo = before.AccountCargo with { Capacity = tier.Capacity } };
        return new(true, 0, new(before, after, create ? "account_cargo_create" : "account_cargo_upgrade"));
    }

    public InventoryResult Gold(Character character, int amount, bool deposit)
    {
        var before = character.Inventory;
        if (amount <= 0 || before.AccountCargo.Capacity == 0)
            return InventoryResult.Reject(10);
        long characterGold = (long)before.Gold + (deposit ? -amount : amount);
        long cargoGold = (long)before.AccountCargo.Gold + (deposit ? amount : -amount);
        int carryLimit = new CurrencyService(goldLimits).CarryLimit(character);
        if (characterGold < 0 || characterGold > uint.MaxValue || cargoGold < 0 || cargoGold > int.MaxValue ||
            deposit && cargoGold > cargo.GoldLimit(before.AccountCargo.Capacity) || !deposit && characterGold > carryLimit)
            return InventoryResult.Reject(10);
        var after = before with { Gold = checked((uint)characterGold), AccountCargo = before.AccountCargo with { Gold = (int)cargoGold } };
        return new(true, 0, new(before, after, deposit ? "account_cargo_deposit_gold" : "account_cargo_withdraw_gold"));
    }

    public InventoryResult PersonalUpgrade(Character character, short slot)
    {
        var before = character.Inventory;
        var item = before.Find(new(InventoryListType.Main, slot));
        if (item is null || !item.Core.IsStackable || item.Core.EquipmentLockId != 0 || item.Core.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            return InventoryResult.Reject(17);
        ushort target = catalog.Get(item.Core.ItemId).PersonalCargoUpgradeTarget;
        if (target <= before.CargoCapacity || target > 264)
            return InventoryResult.Reject(19);
        var consumed = InventoryDeleteService.Plan(before, item.Position, item.Core.Value, 1);
        if (!consumed.Accepted)
            return InventoryResult.Reject(17);
        var after = consumed.Plan!.After with { CargoCapacity = target };
        return new(true, 0, new(before, after, "personal_cargo_upgrade"));
    }
}
