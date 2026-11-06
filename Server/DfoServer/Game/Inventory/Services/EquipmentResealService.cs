using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class EquipmentResealService(ItemCatalog items, EquipmentResealCatalog catalog,
    EpicPieceCatalog pieces, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public bool Handles(Character character, EquipmentQualityRequest request) => !request.RandomOptions &&
        (request is { MaterialSlot: 0, ClientState: 1 } ||
            character.Inventory.Find(new(InventoryListType.Main, request.MaterialSlot)) is { } source &&
            items.Get(source.Core.ItemId).ResealConsumable is not null);

    public InventoryResult Plan(Character character, EquipmentQualityRequest request)
    {
        var before = character.Inventory;
        var targetSlot = new InventorySlot(InventoryListType.Main, request.TargetSlot);
        var sourceSlot = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var target = before.Find(targetSlot);
        var now = clock.GetUtcNow();
        bool soul = request is { MaterialSlot: 0, ClientState: 1 };
        if (request.RandomOptions || target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Core.SealFlag: 0 } ||
            target.Core.ItemId != request.ItemId || target.Core.IsRental || target.IsExpired(now.ToUnixTimeSeconds()) ||
            target.Core.RandomChange.Type != 0 || !ItemSlotBoundService.Allows(before, targetSlot, target.Core.Kind)) return InventoryResult.Reject(19);
        if (target.Core.EquipmentLockId != 0 || target.Core.SortLockFlag != 0) return InventoryResult.Reject(214);
        var template = items.Get(target.Core.ItemId);
        int effectiveLevel = template.MinimumLevel + unchecked((sbyte)target.Core.EmancipateLevel);
        if (effectiveLevel is < 1 or >= 100) return InventoryResult.Reject(19);
        template = template with { MinimumLevel = (byte)effectiveLevel };
        if (!template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) || template.PvpFreeEquipment ||
            template.GrowthEquipment || template.PeriodEquipment || target.Core.TradeRestriction != 0 || template.EquipmentSlot is < 12 or > 25)
            return InventoryResult.Reject(19);
        int count = target.Core.Attr >> 5;
        InventorySnapshot after;
        bool oneShot = false;
        if (soul)
        {
            var cost = catalog.SoulCost(template, count);
            if (cost is null) return InventoryResult.Reject(19);
            var paid = InventoryMaterialConsumptionService.Plan(before, [cost], pieces, now);
            if (!paid.Accepted) return InventoryResult.Reject(21);
            after = paid.Plan!.After;
        }
        else
        {
            var source = before.Find(sourceSlot);
            if (sourceSlot == targetSlot || source is not { Uid: > 0 } || source.IsExpired(now.ToUnixTimeSeconds()) ||
                source.Core.EquipmentLockId != 0 || !source.Core.IsStackable ||
                !ItemSlotBoundService.Allows(before, sourceSlot, source.Core.Kind)) return InventoryResult.Reject(17);
            var material = items.Get(source.Core.ItemId);
            if (material.ResealConsumable is not { } wax || !wax.Allows(template) || !material.AllowsActivation(character.Level, character.Job) ||
                !InventoryMoveService.RequiredSkillSatisfied(character, material)) return InventoryResult.Reject(19);
            oneShot = wax.OneShot;
            int amount = oneShot ? 1 : catalog.WaxCost(template, count);
            if (amount <= 0 || !oneShot && count >= catalog.Limit(template)) return InventoryResult.Reject(19);
            var paid = InventoryDeleteService.Plan(before, sourceSlot, source.Core.ItemId, amount);
            if (!paid.Accepted) return InventoryResult.Reject(21);
            after = paid.Plan!.After;
        }
        if (!oneShot && count >= 7) return InventoryResult.Reject(19);
        var core = target.Core with { SealFlag = 1, Attr = checked((byte)((target.Core.Attr & 31) | (oneShot ? count : count + 1) << 5)) };
        after = after with { Items = after.Items.Replace(target, target with { Core = core }) };
        long? deadline = before.Items.Where(item => item.ExpireTime > 0 && (item == target || after.Find(item.Position) != item))
            .Select(item => (long?)item.ExpireTime).Min();
        return new(true, 0, new(before, after, soul ? "equipment_reseal_soul" : "equipment_reseal_wax",
            new(character, deadline, clock, requirePosition: true)));
    }
}
