using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryDarkRevelationService(DarkRevelationCatalog revelations, EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Apply(Character character, DarkRevelationRequest request, DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? Clock.GetUtcNow();
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, request.Slot));
        var weapon = revelations.Weapon(request.ItemId);
        if (source is null || source.Core.ItemId != request.ItemId || source.Core.Kind != ItemKind.Equipment ||
            source.Core.IsRental || source.IsExpired(now.ToUnixTimeSeconds()) || weapon is null ||
            request.PowerSlot >= weapon.Slots || !revelations.Conditions.TryGetValue(request.PowerSlot, out var conditions) ||
            !revelations.AbilityGroups.TryGetValue(request.Ability, out string? group))
            return InventoryResult.Reject(1);
        if (source.Core.EquipmentLockId != 0) return InventoryResult.Reject(0xD6);
        uint abilities = source.Core.Extension114.Value;
        for (int slot = 0; slot < 3; slot++)
        {
            int existing = (byte)(abilities >> (slot * 8));
            if (slot != request.PowerSlot && revelations.AbilityGroups.TryGetValue(existing, out string? other) && group == other)
                return InventoryResult.Reject(1);
        }
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        foreach (var condition in conditions)
        {
            short equipmentSlot = ItemCatalog.Slot(condition.EquipmentType.Trim('[', ']'));
            var equipped = before.Find(new(InventoryListType.Equipment, equipmentSlot));
            if (equipped is null || !condition.Items.Contains(equipped.Core.ItemId) || equipped.IsExpired(now.ToUnixTimeSeconds()))
                return InventoryResult.Reject(1);
            if (equipped.ExpireTime > 0)
                deadline = Math.Min(deadline ?? long.MaxValue, equipped.ExpireTime);
        }
        int shift = request.PowerSlot * 8;
        bool first = (byte)(abilities >> shift) == 0;
        var paid = InventoryMaterialConsumptionService.Plan(before,
            first ? revelations.FirstMaterials : revelations.RepeatMaterials, pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(0x11);
        uint updated = abilities & ~(0xFFu << shift) | (uint)request.Ability << shift;
        var after = paid.Plan!.After;
        after = after with
        {
            Items = after.Items.Replace(source, source with
            {
                Core = source.Core with { Extension114 = source.Core.Extension114 with { Value = updated } }
            })
        };
        var index = new InventoryIndex(after);
        foreach (var item in before.Items)
            if (item.ExpireTime > 0 && item != index.Find(item.Position))
                deadline = Math.Min(deadline ?? long.MaxValue, item.ExpireTime);
        return new(true, 0, new InventoryPlan(before, after, "equipment_dark_revelation",
            new(character, deadline, Clock, requirePosition: true)));
    }
}
