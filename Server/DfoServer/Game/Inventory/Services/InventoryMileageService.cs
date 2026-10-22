using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryMileageService(ItemCatalog catalog, TownCatalog? towns = null, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public InventoryResult Plan(Character character, InventorySlot slot, bool inTown)
    {
        var source = character.Inventory.Find(slot);
        if (source is null || catalog.ActionConsumable(source.Core.ItemId) is not { Label: "[add mileage]", Parameters: [> 0] } definition)
            return InventoryResult.Reject(19);
        long total = (long)character.Inventory.AccountWallet.Mileage + definition.Parameters[0];
        // 礼盒是有成本的资产，溢出拒绝整笔，不能扣盒后把其面额饱和裁掉。
        if (total > int.MaxValue) return InventoryResult.Reject(19);
        string place = !inTown ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
            ? "[seria room]" : "[village]";
        var paid = InventoryActionConsumableService.Plan(character, slot, definition, place, clock);
        if (!paid.Accepted) return paid;
        var after = paid.Plan!.After with { AccountWallet = paid.Plan.After.AccountWallet.Set(CurrencyKind.Mileage, (int)total) };
        return new(true, 0, new(character.Inventory, after, "mileage_item", paid.Plan.CharacterPrecondition));
    }
}
