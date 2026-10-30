using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarEmblemService(ItemCatalog items, TimeProvider? clock = null)
{
    internal const ushort Rejection = 4;

    public InventoryResult Use(Character character, AvatarEmblemRequest request,
        out AvatarEmblemReceipt? receipt, out string? rejection)
    {
        receipt = null;
        rejection = "target";
        long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var target = before.Find(request.Target);
        if (request.Target.List is not (InventoryListType.Avatar or InventoryListType.Equipment) ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Avatar, Core.EquipmentLockId: 0, Avatar: not null } ||
            target.Core.ItemId != request.TargetItemId || target.IsExpired(now) ||
            !ItemSlotBoundService.Allows(before, target.Position, ItemKind.Avatar) ||
            request.Emblems.IsDefault || request.Emblems.Length is < 1 or > 5)
            return InventoryResult.Reject(Rejection);
        try
        {
            var template = items.Get(target.Core.ItemId);
            if (template.Id != target.Core.ItemId || template.Kind != ItemKind.Avatar)
                return InventoryResult.Reject(Rejection);
            var detail = target.Avatar;
            AvatarSocket[] sockets = [detail.Socket0, detail.Socket1, detail.Socket2, detail.Socket3, detail.Socket4];
            var usedSockets = new HashSet<byte>();
            var materials = new Dictionary<short, (InventoryItem Item, int Count)>();
            var materialOrder = new List<short>();
            long? expiry = target.ExpireTime > 0 ? target.ExpireTime : null;
            foreach (var entry in request.Emblems)
            {
                rejection = "socket";
                if (entry.SocketIndex > 4 || !usedSockets.Add(entry.SocketIndex) ||
                    sockets[entry.SocketIndex] is not { Type: > 0, EmblemId: >= -1 })
                    return InventoryResult.Reject(Rejection);
                rejection = "material";
                var material = before.Find(entry.Material);
                if (material is not { Uid: > 0, Core.Kind: ItemKind.Emblem, Core.Count: > 0, Core.EquipmentLockId: 0 } ||
                    material.Core.ItemId != entry.MaterialItemId || material.IsExpired(now) ||
                    !ItemSlotBoundService.Allows(before, material.Position, ItemKind.Emblem))
                    return InventoryResult.Reject(Rejection);
                var emblem = items.Get(material.Core.ItemId);
                if (emblem.Id != material.Core.ItemId || emblem.Kind != ItemKind.Emblem ||
                    (emblem.EmblemTargetSockets & sockets[entry.SocketIndex].Type) == 0)
                {
                    rejection = "incompatible-emblem";
                    return InventoryResult.Reject(Rejection);
                }
                int required = materials.TryGetValue(entry.MaterialSlot, out var aggregate) ? aggregate.Count + 1 : 1;
                if (required > material.Core.Count)
                {
                    rejection = "insufficient-emblem";
                    return InventoryResult.Reject(Rejection);
                }
                if (required == 1) materialOrder.Add(entry.MaterialSlot);
                materials[entry.MaterialSlot] = (material, required);
                if (material.ExpireTime > 0 && (expiry is null || material.ExpireTime < expiry))
                    expiry = material.ExpireTime;
                // 保留孔型、克隆来源及染色，仅覆盖客户端明确选择的孔；旧徽章不返还。
                sockets[entry.SocketIndex] = sockets[entry.SocketIndex] with { EmblemId = emblem.Id };
            }
            var after = before;
            foreach (short slot in materialOrder)
            {
                var aggregate = materials[slot];
                var consumed = InventoryDeleteService.Plan(after, aggregate.Item.Position, aggregate.Item.Core.ItemId, aggregate.Count);
                if (!consumed.Accepted) return InventoryResult.Reject(Rejection);
                after = consumed.Plan!.After;
            }
            var changed = target with
            {
                Avatar = detail with
                {
                    Socket0 = sockets[0], Socket1 = sockets[1], Socket2 = sockets[2], Socket3 = sockets[3], Socket4 = sockets[4]
                }
            };
            after = after with { Items = after.Items.Replace(target, changed) };
            after.Validate();
            receipt = new(target.Position, [.. materialOrder.Select(slot =>
                new AvatarEmblemRemaining(slot, after.Find(materials[slot].Item.Position)?.Core.Count ?? 0))]);
            rejection = null;
            return AvatarPresetService.SyncActive(new(true, 0,
                new(before, after, "avatar_emblem_use", new(character, expiry, clock))));
        }
        catch (InvalidDataException)
        {
            rejection = "invalid-resource";
            return InventoryResult.Reject(Rejection);
        }
    }
}
