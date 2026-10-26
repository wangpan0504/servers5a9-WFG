using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryAvatarAbilityChangeService(ItemCatalog items, AvatarPackageCatalog avatars, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Apply(Character character, AvatarAbilityChangeRequest request, out string? rejection)
    {
        rejection = "slot-or-instance";
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        if (source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } || source.Core.ItemId != request.SourceId ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(now) ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Avatar, Avatar: not null } || target.Core.ItemId != request.AvatarId ||
            target.Core.EquipmentLockId != 0 || target.IsExpired(now) || source.Uid == target.Uid ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind))
            return InventoryResult.Reject(0);
        try
        {
            rejection = "source-qualification";
            var template = items.Get(source.Core.ItemId);
            if (!InventoryAvatarPackageService.SourceEligible(character, source, template)) return InventoryResult.Reject(0);
            rejection = "target-or-ability";
            if (template.AvatarAbilityChange is not { } rule || !rule.Allows(items.Get(target.Core.ItemId)) ||
                rule.OnlyFinite && target.ExpireTime <= 0 ||
                avatars.GetAvatar(target.Core.ItemId) is not { PendingRule: null, RequiresAbilityChoice: true } definition ||
                !definition.Abilities.Contains(request.Ability)) return InventoryResult.Reject(0);
            var consumed = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
            if (!consumed.Accepted) return InventoryResult.Reject(0);
            // 装扮沿既有条目合同在Durability保存可选属性；不重建UID或详情、孔位、颜色和期限。
            var after = consumed.Plan!.After;
            after = after with { Items = after.Items.Replace(target, target with { Core = target.Core with { Durability = request.Ability } }) };
            long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
            if (target.ExpireTime > 0) deadline = Math.Min(deadline ?? long.MaxValue, target.ExpireTime);
            rejection = null;
            return new(true, 0, new(before, after, "avatar_ability_change",
                new(character, deadline, Clock, requirePosition: true)));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            rejection = "resource-invalid";
            return InventoryResult.Reject(0);
        }
    }
}
