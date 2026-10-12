using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEnhancementService(ItemCatalog items, EnhancementCatalog enhancements, Random? random = null, TimeProvider? clock = null)
{
    internal const ushort Rejection = 23;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, EnhancementRequest request, out EnhancementReceipt? receipt)
    {
        receipt = null;
        var source = character.Inventory.Find(new(InventoryListType.Main, request.MaterialSlot));
        var target = character.Inventory.Find(request.Target);
        if (source is null || target is null) return InventoryResult.Reject(Rejection);
        var definition = enhancements.Get(source.Core.ItemId);
        if (definition is null || !definition.Available || !enhancements.Allows(target.Core.ItemId, request.Mode)) return InventoryResult.Reject(Rejection);
        return Plan(character, request, Clock.GetUtcNow().ToUnixTimeSeconds(), definition,
            items.Get(source.Core.ItemId), items.Get(target.Core.ItemId), () => (random ?? Random.Shared).Next(10000), out receipt);
    }

    internal static InventoryResult Plan(Character character, EnhancementRequest request, long now, EnhancementDefinition definition,
        ItemTemplate sourceTemplate, ItemTemplate targetTemplate, Func<int> roll, out EnhancementReceipt? receipt)
    {
        receipt = null;
        var before = character.Inventory;
        var sourcePosition = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var source = before.Find(sourcePosition);
        var target = before.Find(request.Target);
        if (request.Target.List is not (InventoryListType.Main or InventoryListType.Equipment) || sourcePosition == request.Target ||
            request.ProtectionSlot != -1 || request.AuxiliarySlot0 != -1 || request.AuxiliarySlot1 != -1 ||
            source is null || target is null || source.Uid <= 0 || target.Uid <= 0 || !source.Core.IsStackable || source.Core.Count <= 0 ||
            target.Core.Kind != ItemKind.Equipment || target.Core.ItemId != request.TargetItemId ||
            source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0 || source.IsExpired(now) || target.IsExpired(now) ||
            sourceTemplate.Id != source.Core.ItemId || sourceTemplate.Kind != source.Core.Kind || targetTemplate.Id != target.Core.ItemId ||
            targetTemplate.Kind != target.Core.Kind || definition.ItemId != source.Core.ItemId || definition.Mode != request.Mode ||
            !definition.TryTargetLevel(target.Core.Upgrade, out byte desired) ||
            targetTemplate.EquipmentSlot is not (12 or >= 14 and <= 23 or 25) ||
            targetTemplate.MinimumLevel < definition.MinimumItemLevel || targetTemplate.MinimumLevel > definition.MaximumItemLevel ||
            !definition.AllowedItems.IsEmpty && !definition.AllowedItems.Contains(target.Core.ItemId) ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) || !ItemSlotBoundService.Allows(before, target.Position, target.Core.Kind) ||
            sourceTemplate.UseForbidden || character.Level < sourceTemplate.MinimumLevel || character.Job >= 16 || (sourceTemplate.Jobs & (1 << character.Job)) == 0 ||
            !InventoryMoveService.RequiredSkillSatisfied(character, sourceTemplate))
            return InventoryResult.Reject(Rejection);
        if (request.Mode == EnhancementMode.Reinforce ? target.Core.AmplifyType != 0 :
            target.Core.AmplifyType is < 1 or > 4 || target.Core.AmplifyValue == 0)
            return InventoryResult.Reject(Rejection);
        int value = roll();
        if (value is < 0 or >= 10000) throw new InvalidOperationException("强化/增幅概率源超出范围");
        bool succeeded = value < definition.SuccessWeight;
        byte oldLevel = target.Core.Upgrade, newLevel = succeeded ? desired : oldLevel;
        var paid = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!paid.Accepted) return InventoryResult.Reject(Rejection);
        // Attr低五位是强化/增幅等级；AmplifyValue为基础异次元值，不能累加本次等级效果。
        var changed = target with { Core = target.Core with { Attr = (byte)((target.Core.Attr & 0xE0) | newLevel) } };
        var after = paid.Plan!.After with { Items = paid.Plan.After.Items.Replace(target, changed) };
        after.Validate();
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (target.ExpireTime > 0) deadline = deadline is null ? target.ExpireTime : Math.Min(deadline.Value, target.ExpireTime);
        receipt = new(request.Mode, request.Target, request.MaterialSlot, source.Core.Count - 1, oldLevel, newLevel, succeeded);
        return new(true, 0, new(before, after, "equipment_enhancement_ticket", new(character, deadline)));
    }
}
