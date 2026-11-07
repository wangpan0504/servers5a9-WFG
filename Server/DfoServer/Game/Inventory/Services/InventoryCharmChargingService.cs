using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryCharmChargingService(Func<int, ItemTemplate> items,
    Func<int, ActionConsumableDefinition?> chargingItems, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, InventorySlot targetSlot, InventorySlot sourceSlot)
    {
        var now = Clock.GetUtcNow();
        var target = character.Inventory.Find(targetSlot);
        var source = character.Inventory.Find(sourceSlot);
        if (targetSlot == sourceSlot || targetSlot.List != InventoryListType.Main ||
            target is not { Uid: > 0, Core.Kind: ItemKind.Equipment, Avatar: null, Creature: null } ||
            target.Core.EquipmentLockId != 0 || target.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(character.Inventory, targetSlot, target.Core.Kind) ||
            source is not { Core.Kind: ItemKind.Consumable })
            return InventoryResult.Reject(1) with { RejectReason = "invalid_charm_target" };
        var template = items(target.Core.ItemId);
        if (!template.IsQuickSlotEquipment || template.QuickSlotEquipment is not { Heraldry: false })
            return InventoryResult.Reject(1) with { RejectReason = "invalid_charm_target" };
        if (template.Durability == 0)
            return InventoryResult.Reject(1) with { RejectReason = "permanent_quick_slot_equipment" };
        var definition = chargingItems(source.Core.ItemId);
        if (definition is not { Label: "[charm charging]", Parameters: [> 0, > 0, ..] })
            return InventoryResult.Reject(1) with { RejectReason = "invalid_charm_charging_item" };
        // 01B3CC10读取条目offset11对应的耐久，并按券的首参数检查恢复上限。
        // 高阶券可突破模板初始100；只受该券上限和物品u16字段边界约束，不修改品级Value。
        int maximum = Math.Min(ushort.MaxValue, definition.Parameters[0]);
        if (target.Core.Durability >= maximum)
            return InventoryResult.Reject(4) with { RejectReason = "charm_energy_full" };
        ushort energy = (ushort)Math.Min(maximum, (long)target.Core.Durability + definition.Parameters[1]);
        // 使用地点由Session的STK统一入口核对；沿原消费器保存冷却、次数和使用事实。
        var consumed = InventoryActionConsumableService.Plan(character, sourceSlot, definition, "", Clock, now);
        if (!consumed.Accepted)
            return consumed with { RejectReason = "charm_charging_source_rejected" };
        var plan = consumed.Plan!;
        var after = plan.After with { Items = plan.After.Items.Replace(target,
            target with { Core = target.Core with { Durability = energy } }) };
        long? deadline = plan.CharacterPrecondition!.QualificationExpiresAt;
        if (target.ExpireTime > 0) deadline = Math.Min(deadline ?? long.MaxValue, target.ExpireTime);
        return new(true, 0, new(character.Inventory, after, "charm_energy_charged",
            new(character, deadline, Clock, requirePosition: true)) { UsedItems = plan.UsedItems });
    }
}
