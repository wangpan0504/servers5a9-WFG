using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Currency;

internal sealed class CurrencyService(GoldLimitCatalog limits)
{
    public const int UpgradeCost = 5_000_000;
    public const int BaseAuctionLimit = 400_000_000;
    public static int ExpandedLimit(byte stage) => stage is >= 1 and <= 4 ? (stage + 4) * 100_000_000 : 0;
    public static byte UpgradeStage(InventorySnapshot inventory)
    {
        int limit = Math.Min(inventory.GoldCarryLimit, inventory.AuctionGoldLimit);
        for (byte stage = 4; stage > 0; stage--)
            if (limit >= ExpandedLimit(stage)) return stage;
        return 0;
    }
    public int CarryLimit(Character character) => Math.Max(limits.CarryLimit(character.Level), character.Inventory.GoldCarryLimit);
    public InventoryResult Initialize(Character character) => new(true, 0, new(character.Inventory,
        character.Inventory with { GoldCarryLimit = CarryLimit(character) }, "gold_limit_initialize"));
    public InventoryResult Grant(Character character, CurrencyKind kind, int amount)
    {
        if (amount < 0) return InventoryResult.Reject();
        var before = character.Inventory;
        long current = CurrencyWallet.Get(before, kind);
        long limit = kind == CurrencyKind.Gold ? Math.Max(current, CarryLimit(character)) : int.MaxValue;
        long balance = Math.Min(current + amount, limit);
        return new(true, 0, new(before, CurrencyWallet.Set(before, kind, balance), "currency_grant"));
    }
    public static InventoryResult Spend(InventorySnapshot inventory, CurrencyKind kind, int amount)
    {
        if (amount < 0 || CurrencyWallet.Get(inventory, kind) < amount) return InventoryResult.Reject();
        return new(true, 0, new(inventory, CurrencyWallet.Set(inventory, kind, CurrencyWallet.Get(inventory, kind) - amount), "currency_spend"));
    }
    public InventoryResult Upgrade(Character character, byte requestedStage)
    {
        var before = character.Inventory;
        byte stage = UpgradeStage(before);
        if (character.Level < 60 || stage >= 4 || requestedStage != stage + 1 || before.Gold < UpgradeCost)
            return InventoryResult.Reject();
        int expanded = ExpandedLimit(requestedStage);
        var after = before with { Gold = before.Gold - UpgradeCost, GoldCarryLimit = expanded, AuctionGoldLimit = expanded };
        return new(true, 0, new(before, after, "gold_limit_upgrade"));
    }
}
