using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEnchantmentService(ItemCatalog items, EnchantmentCatalog enchantments, TimeProvider? clock = null)
{
    // 272有公共u16拒绝分支；细分错误文案尚待客户端验收，不复用其他版本的宝珠错误表。
    internal const ushort Rejection = 23;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, EnchantmentRequest request, long utcSeconds)
    {
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        if (request.Source.List != InventoryListType.Main || request.Target.List is not
            (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.Pet) || request.Source == request.Target ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Core.Kind != ItemKind.Consumable ||
            !source.Core.IsStackable || target.Core.IsStackable || source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 ||
            source.IsExpired(utcSeconds) || target.IsExpired(utcSeconds))
            return InventoryResult.Reject(Rejection);
        var definition = enchantments.Get(source.Core.ItemId);
        if (definition is null) return InventoryResult.Reject(Rejection);
        return Plan(character, request, utcSeconds, definition, items.Get(source.Core.ItemId), items.Get(target.Core.ItemId));
    }

    internal static InventoryResult Plan(Character character, EnchantmentRequest request, long now,
        EnchantmentDefinition definition, ItemTemplate beadTemplate, ItemTemplate targetTemplate)
    {
        var before = character.Inventory;
        var source = before.Find(request.Source);
        var target = before.Find(request.Target);
        if (request.Source.List != InventoryListType.Main || request.Target.List is not
            (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.Pet) || request.Source == request.Target ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || source.Core.Kind != ItemKind.Consumable ||
            source.Core.Count <= 0 || target.Core.IsStackable || source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 ||
            source.IsExpired(now) || target.IsExpired(now) || beadTemplate.Id != source.Core.ItemId || beadTemplate.Kind != source.Core.Kind ||
            targetTemplate.Id != target.Core.ItemId || targetTemplate.Kind != target.Core.Kind || definition.BeadItemId != source.Core.ItemId ||
            definition.CardItemId <= 0 || !definition.Targets.Contains((targetTemplate.Kind, targetTemplate.EquipmentSlot)) ||
            !definition.LimitedItems.IsEmpty && !definition.LimitedItems.Contains(target.Core.ItemId) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) || beadTemplate.UseForbidden ||
            character.Level < beadTemplate.MinimumLevel || (beadTemplate.Jobs & (1 << character.Job)) == 0 ||
            !InventoryMoveService.RequiredSkillSatisfied(character, beadTemplate))
            return InventoryResult.Reject(Rejection);
        var consumed = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!consumed.Accepted) return InventoryResult.Reject(Rejection);
        var changed = target with { Core = target.Core with { EnchantCardId = definition.CardItemId, EnchantUpgrade = definition.Upgrade } };
        var after = consumed.Plan!.After with { Items = consumed.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        return new(true, 0, new(before, after, "enchant_by_bead", new(character, deadline)));
    }
}
