using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryPeriodExtensionService(ItemCatalog catalog, TownCatalog? towns = null, TimeProvider? time = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public InventoryResult Plan(Character character, InventorySlot sourceSlot, InventorySlot targetSlot,
        ActionConsumableDefinition? definition, bool inTown)
    {
        var now = Clock.GetUtcNow();
        var source = character.Inventory.Find(sourceSlot);
        var target = character.Inventory.Find(targetSlot);
        if (definition is not { Label: "[extend item period]", Parameters: [0, > 0] } ||
            sourceSlot == targetSlot || source is null || target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Avatar: null, Creature: null } ||
            targetSlot.List is not (InventoryListType.Main or InventoryListType.Equipment) || target.Core.EquipmentLockId != 0 ||
            !ItemSlotBoundService.Allows(character.Inventory, targetSlot, target.Core.Kind) ||
            !definition.AllowedTargets.Contains(target.Core.ItemId) || target.ExpireTime <= 0 || target.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        var template = catalog.Get(target.Core.ItemId);
        if (template.Kind != ItemKind.Equipment || template.Expiration.Days <= 0 || template.Expiration.DailyDelete != 0 ||
            template.Expiration.WeeklyDeleteDay is not null)
            return InventoryResult.Reject(19);
        int expires = (int)Math.Min(ItemExpiration.MaximumUnixTime, target.ExpireTime + (long)definition.Parameters[1] * 86400);
        if (expires <= target.ExpireTime) return InventoryResult.Reject(19);
        string place = !inTown ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
            ? "[seria room]" : "[village]";
        var paid = InventoryActionConsumableService.Plan(character, sourceSlot, definition, place, Clock, now);
        if (!paid.Accepted) return paid;
        var after = paid.Plan!.After;
        var extended = target with { Core = target.Core with { ExpireTime = expires } };
        long deadline = target.ExpireTime;
        if (source.ExpireTime > 0) deadline = Math.Min(deadline, source.ExpireTime);
        if (definition.DailyLimit > 0) deadline = Math.Min(deadline, DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        return new(true, 0, new(character.Inventory, after with { Items = after.Items.Replace(target, extended) },
            "item_period_extended", new(character, deadline, Clock, requirePosition: true)));
    }
}
