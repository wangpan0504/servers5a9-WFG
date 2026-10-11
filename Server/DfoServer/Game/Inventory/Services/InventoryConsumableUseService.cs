using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryConsumableUseService(RecoveryConsumableCatalog consumables, TimeProvider? time = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;
    public bool TryDefinition(int id, out RecoveryConsumableDefinition definition) => consumables.TryGet(id, out definition);

    public InventoryResult Plan(Character character, ConsumableUseRequest request, long now)
    {
        var before = character.Inventory;
        var source = before.Find(request.Position);
        if (request.Argument != 0 || request.OpaqueCheck > 32767 || request.Position.List != InventoryListType.Main || source is null || source.Uid <= 0 ||
            source.Core.ItemId != request.ItemId || source.Core.Kind != ItemKind.Consumable || source.Core.Count <= 0 ||
            !ItemSlotBoundService.Allows(before, request.Position, ItemKind.Consumable) || source.Core.EquipmentLockId != 0 ||
            source.Core.IsExpired(now) || !consumables.TryGet(request.ItemId, out var definition) || character.Level < definition.MinimumLevel)
            return InventoryResult.Reject(123);
        var deletion = InventoryDeleteService.Plan(before, request.Position, request.ItemId, 1);
        if (!deletion.Accepted || deletion.Plan is null) return InventoryResult.Reject(123);
        return new(true, 0, new(before, deletion.Plan.After, "use_recovery_consumable", new(character)));
    }
}
