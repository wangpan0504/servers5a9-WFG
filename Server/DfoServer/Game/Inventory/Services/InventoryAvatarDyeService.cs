using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarDyeService(ItemCatalog items, AvatarDyeCatalog dyes)
{
    public InventoryResult Apply(Character character, AvatarDyeRequest request, out string? rejection, DateTimeOffset? utcNow = null)
    {
        rejection = "slot-or-instance";
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        long now = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (source is null || !source.Core.IsStackable || source.Core.Count < 1 || source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            target is not { Core.Kind: ItemKind.Avatar, Avatar: not null } || target.Core.EquipmentLockId != 0 || target.IsExpired(now))
            return InventoryResult.Reject(19);
        try
        {
            rejection = "source-qualification";
            if (!InventoryAvatarPackageService.SourceEligible(character, source, items.Get(source.Core.ItemId)))
                return InventoryResult.Reject(19);
            rejection = "dye-definition";
            var dye = dyes.Get(source.Core.ItemId);
            if (dye is null) return InventoryResult.Reject(19);
            rejection = "target-not-dyeable";
            if (items.Get(target.Core.ItemId).Kind != ItemKind.Avatar || !dyes.CanDye(target.Core.ItemId))
                return InventoryResult.Reject(19);
            rejection = "target-not-colored";
            if (dye.Color == 0 && target.Avatar.Color1 == 0 && target.Avatar.Color2 == 0)
                return InventoryResult.Reject(19);
            var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.Value, 1);
            if (!paid.Accepted) { rejection = "source-delete"; return paid; }
            var after = paid.Plan!.After;
            after = after with { Items = after.Items.Replace(target, target with { Avatar = target.Avatar with { Color1 = dye.Color, Color2 = 0 } }) };
            long? expires = source.ExpireTime > 0 ? source.ExpireTime : null;
            if (target.ExpireTime > 0 && (expires is null || target.ExpireTime < expires)) expires = target.ExpireTime;
            rejection = null;
            return new(true, 0, new(before, after, "avatar_dye", new(character, expires)));
        }
        catch (InvalidDataException) { rejection = "invalid-resource"; return InventoryResult.Reject(19); }
    }
}
