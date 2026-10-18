using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEquipmentEmblemService(ItemCatalog items, TimeProvider? clock = null)
{
    internal const ushort Rejection = 4;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult OpenSockets(Character character, EquipmentSocketRequest request,
        out EquipmentSocketReceipt? receipt, out string? rejection, long? utcSeconds = null)
    {
        receipt = null;
        rejection = "target";
        long now = utcSeconds ?? Clock.GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var target = FindTarget(before, request.TargetSlot, request.TargetItemId);
        if (target is null || !TargetEligible(before, target, now) ||
            !ValidState(target.Core, requireOpen: false))
            return InventoryResult.Reject(Rejection);
        try
        {
            rejection = "target-template";
            var targetTemplate = items.Get(target.Core.ItemId);
            if (!TrySocketSpec(targetTemplate, out var spec))
                return InventoryResult.Reject(Rejection);

            rejection = "already-open";
            if (target.Core.EmblemSocketCount != 0)
                return InventoryResult.Reject(Rejection);

            rejection = "material";
            var material = before.Find(request.Material);
            if (material is null || material.Position == target.Position || !material.Core.IsStackable ||
                material.Core.Count < 1 || material.Core.EquipmentLockId != 0 || material.IsExpired(now) ||
                !ItemSlotBoundService.Allows(before, material.Position, material.Core.Kind))
                return InventoryResult.Reject(Rejection);
            var materialTemplate = items.Get(material.Core.ItemId);
            if (materialTemplate.Id != material.Core.ItemId || materialTemplate.Kind != material.Core.Kind)
                return InventoryResult.Reject(Rejection);

            var consumed = InventoryDeleteService.Plan(before, material.Position, material.Core.ItemId, 1);
            if (!consumed.Accepted)
                return InventoryResult.Reject(Rejection);
            var changed = target with
            {
                Core = target.Core with
                {
                    EmblemSocketCount = spec.Limit,
                    Emblem1 = -1,
                    Emblem2 = spec.Limit == 2 ? -1 : 0
                }
            };
            var after = consumed.Plan!.After with
            {
                Items = consumed.Plan.After.Items.Replace(target, changed)
            };
            after.Validate();
            receipt = new(target.Position, request.MaterialSlot);
            rejection = null;
            return new(true, 0, new(before, after, "equipment_socket_open",
                new(character, EarliestExpiry(target, material))));
        }
        catch (InvalidDataException)
        {
            rejection = "invalid-resource";
            return InventoryResult.Reject(Rejection);
        }
    }

    public InventoryResult UseEmblems(Character character, EquipmentEmblemRequest request,
        out EquipmentEmblemReceipt? receipt, out string? rejection, long? utcSeconds = null)
    {
        receipt = null;
        rejection = "request";
        if (request.Emblems.IsDefault || request.Emblems.Length is < 1 or > 2)
            return InventoryResult.Reject(Rejection);
        long now = utcSeconds ?? Clock.GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var target = FindTarget(before, request.TargetSlot, request.TargetItemId);
        if (target is null || !TargetEligible(before, target, now) ||
            !ValidState(target.Core, requireOpen: true))
        {
            rejection = "target";
            return InventoryResult.Reject(Rejection);
        }

        try
        {
            rejection = "target-template";
            var targetTemplate = items.Get(target.Core.ItemId);
            if (!TrySocketSpec(targetTemplate, out var spec) ||
                target.Core.EmblemSocketCount > spec.Limit)
                return InventoryResult.Reject(Rejection);

            int[] emblems = [target.Core.Emblem1, target.Core.Emblem2];
            var logicalSockets = new HashSet<int>();
            var materials = new Dictionary<short, (InventoryItem Item, int Count)>();
            var materialOrder = new List<short>();
            long? earliestExpiry = target.ExpireTime > 0 ? target.ExpireTime : null;
            for (int index = 0; index < request.Emblems.Length; index++)
            {
                var entry = request.Emblems[index];
                int logicalSocket = LogicalSocket(spec, target.Core.EmblemSocketCount, entry.SocketIndex);
                rejection = "socket";
                if (logicalSocket < 0 || !logicalSockets.Add(logicalSocket))
                    return InventoryResult.Reject(Rejection);

                rejection = "material";
                var material = before.Find(entry.Material);
                if (material is null || material.Position == target.Position ||
                    material.Core.ItemId != entry.MaterialItemId || material.Core.Kind != ItemKind.Emblem ||
                    material.Core.Count < 1 || material.Core.EquipmentLockId != 0 || material.IsExpired(now) ||
                    !ItemSlotBoundService.Allows(before, material.Position, ItemKind.Emblem))
                    return InventoryResult.Reject(Rejection);
                var emblemTemplate = items.Get(material.Core.ItemId);
                if (emblemTemplate.Id != material.Core.ItemId || emblemTemplate.Kind != ItemKind.Emblem ||
                    (emblemTemplate.EmblemTargetSockets & spec.Type) == 0)
                {
                    rejection = "incompatible-emblem";
                    return InventoryResult.Reject(Rejection);
                }

                int required = 1;
                if (materials.TryGetValue(entry.MaterialSlot, out var aggregate))
                    required = aggregate.Count + 1;
                else
                    materialOrder.Add(entry.MaterialSlot);
                if (required > material.Core.Count)
                {
                    rejection = "insufficient-emblem";
                    return InventoryResult.Reject(Rejection);
                }
                materials[entry.MaterialSlot] = (material, required);
                if (material.ExpireTime > 0 && (earliestExpiry is null || material.ExpireTime < earliestExpiry))
                    earliestExpiry = material.ExpireTime;
                emblems[logicalSocket] = material.Core.ItemId;
            }

            var after = before;
            foreach (short slot in materialOrder)
            {
                var aggregate = materials[slot];
                var consumed = InventoryDeleteService.Plan(after, aggregate.Item.Position,
                    aggregate.Item.Core.ItemId, aggregate.Count);
                if (!consumed.Accepted)
                {
                    rejection = "material-delete";
                    return InventoryResult.Reject(Rejection);
                }
                after = consumed.Plan!.After;
            }
            var changed = target with { Core = target.Core with { Emblem1 = emblems[0], Emblem2 = emblems[1] } };
            after = after with { Items = after.Items.Replace(target, changed) };
            after.Validate();
            var remaining = materialOrder.Select(slot =>
            {
                var position = materials[slot].Item.Position;
                return new EquipmentEmblemRemaining(slot, after.Find(position)?.Core.Count ?? 0);
            }).ToImmutableArray();
            receipt = new(target.Position, remaining);
            rejection = null;
            return new(true, 0, new(before, after, "equipment_emblem_use",
                new(character, earliestExpiry)));
        }
        catch (InvalidDataException)
        {
            rejection = "invalid-resource";
            return InventoryResult.Reject(Rejection);
        }
    }

    static InventoryItem? FindTarget(InventorySnapshot inventory, short slot, int itemId)
    {
        var main = inventory.Find(new(InventoryListType.Main, slot));
        if (main?.Core.ItemId == itemId)
            return main;
        var equipped = inventory.Find(new(InventoryListType.Equipment, slot));
        return equipped?.Core.ItemId == itemId ? equipped : null;
    }

    static bool TargetEligible(InventorySnapshot inventory, InventoryItem target, long utcSeconds) =>
        target.Uid > 0 && target.Core.Kind == ItemKind.Equipment && !target.Core.IsStackable &&
        target.Core.EquipmentLockId == 0 && !target.IsExpired(utcSeconds) &&
        ItemSlotBoundService.Allows(inventory, target.Position, ItemKind.Equipment);

    static bool ValidState(ItemCore core, bool requireOpen)
    {
        if (core.EmblemSocketCount == 0)
            return !requireOpen && core.Emblem1 == 0 && core.Emblem2 == 0;
        if (core.EmblemSocketCount == 1)
            return OpenValue(core.Emblem1) && core.Emblem2 == 0;
        return core.EmblemSocketCount == 2 && OpenValue(core.Emblem1) && OpenValue(core.Emblem2);
    }

    static bool OpenValue(int value) => value == -1 || value > 0;

    static bool TrySocketSpec(ItemTemplate template, out SocketSpec spec)
    {
        spec = default;
        if (template.Kind != ItemKind.Equipment)
            return false;
        ushort type = template.EquipmentSlot switch
        {
            14 or 16 => 4,
            15 or 19 => 2,
            18 or 21 => 1,
            17 or 20 => 8,
            _ => 16
        };
        spec = new(type, template.EquipmentSlot is 22 or 23 ? (byte)1 : (byte)2);
        return true;
    }

    static int LogicalSocket(SocketSpec spec, byte openCount, byte requested)
    {
        if (requested > 1 || openCount == 0 || openCount > spec.Limit)
            return -1;
        if (spec.Limit == 1)
            return 0;
        return requested < openCount ? requested : -1;
    }

    static long? EarliestExpiry(params InventoryItem[] items)
    {
        long? earliest = null;
        foreach (var item in items)
            if (item.ExpireTime > 0 && (earliest is null || item.ExpireTime < earliest))
                earliest = item.ExpireTime;
        return earliest;
    }

    readonly record struct SocketSpec(ushort Type, byte Limit);
}
