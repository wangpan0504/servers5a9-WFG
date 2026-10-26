using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarSocketService(ItemCatalog items, AvatarPackageCatalog avatars, TimeProvider? clock = null)
{
    internal const ushort Rejection = 4;

    public InventoryResult Open(Character character, AvatarSocketRequest request, out string? rejection)
    {
        rejection = "slot-or-resource";
        var source = character.Inventory.Find(request.Material);
        var target = character.Inventory.Find(request.Target);
        if (source is null || target is null) return InventoryResult.Reject(Rejection);
        try
        {
            return Plan(character, request, items.AvatarSocketOpener(source.Core.ItemId),
                items.Get(target.Core.ItemId), avatars.GetAvatar(target.Core.ItemId), out rejection, clock: clock);
        }
        catch (InvalidDataException)
        {
            rejection = "invalid-resource";
            return InventoryResult.Reject(Rejection);
        }
    }

    internal static InventoryResult Plan(Character character, AvatarSocketRequest request,
        AvatarSocketOpenerDefinition? opener, ItemTemplate template, AvatarPackageItemDefinition avatar,
        out string? rejection, long? utcSeconds = null, TimeProvider? clock = null)
    {
        rejection = "slot-or-instance";
        long now = utcSeconds ?? (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var source = before.Find(request.Material);
        var target = before.Find(request.Target);
        if (source is not { Uid: > 0, Core.IsStackable: true, Core.Count: > 0, Core.EquipmentLockId: 0 } || source.IsExpired(now) ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Avatar, Core.EquipmentLockId: 0, Avatar: not null } ||
            target.Core.ItemId != request.TargetItemId || target.IsExpired(now) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, ItemKind.Avatar))
            return InventoryResult.Reject(Rejection);
        rejection = "source-qualification";
        if (opener is null || !InventoryAvatarPackageService.SourceEligible(character, source, opener.Item))
            return InventoryResult.Reject(Rejection);
        rejection = "target-qualification";
        if (template.Id != target.Core.ItemId || !opener.Allows(template))
            return InventoryResult.Reject(Rejection);
        rejection = "already-open";
        // 任何现有孔位或徽章都不能被开孔器覆盖，重复请求也不能再次消费材料。
        var detail = target.Avatar;
        if (detail.Socket0 != default || detail.Socket1 != default || detail.Socket2 != default ||
            detail.Socket3 != default || detail.Socket4 != default)
            return InventoryResult.Reject(Rejection);
        rejection = "socket-definition";
        if (avatar.PendingRule is not null || avatar.Types.IsDefaultOrEmpty)
            return InventoryResult.Reject(Rejection);
        var candidates = avatar.Types.Select(type => type.Sockets).Where(sockets => sockets != default).Distinct().ToArray();
        if (candidates.Length != 1) return InventoryResult.Reject(Rejection);
        var sockets = candidates[0];
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!paid.Accepted) return paid;
        var changed = target with
        {
            Avatar = detail with
            {
                Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
                Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth),
                Socket4 = AvatarSocket.Empty(sockets.Fifth)
            }
        };
        var after = paid.Plan!.After with { Items = paid.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? expiry = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0 && (expiry is null || target.ExpireTime < expiry)) expiry = target.ExpireTime;
        rejection = null;
        return new(true, 0, new(before, after, "avatar_socket_open", new(character, expiry, clock)));
    }
}
