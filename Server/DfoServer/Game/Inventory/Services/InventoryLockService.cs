using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryLockService
{
    public static InventoryResult Plan(InventorySnapshot before, InventorySlot slot, InventoryLockOperation operation,
        ItemCatalog catalog, long utcSeconds)
    {
        if (slot.List is not (InventoryListType.Main or InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.GuildMedal or InventoryListType.Talisman or InventoryListType.Equipment or InventoryListType.PersonalCargo or InventoryListType.AccountCargo) ||
            slot.Index < 0 || !Enum.IsDefined(operation))
            return InventoryResult.Reject(19);
        if (slot.List == InventoryListType.PersonalCargo && slot.Index >= before.CargoCapacity ||
            slot.List == InventoryListType.AccountCargo && slot.Index >= before.AccountCargo.Capacity)
            return InventoryResult.Reject(19);
        var item = before.Find(slot);
        if (item is null)
            return InventoryResult.Reject(21);
        if (item.IsExpired(utcSeconds))
            return InventoryResult.Reject(19);

        var core = item.Core;
        if (operation is InventoryLockOperation.SortLock or InventoryLockOperation.SortUnlock)
        {
            // Client 分开请求设置和解除排序锁，重复设置保持原状态。
            core = core with { SortLockFlag = operation == InventoryLockOperation.SortLock ? (byte)1 : (byte)0 };
        }
        else
        {
            if (core.IsStackable || slot.List == InventoryListType.AccountCargo)
                return InventoryResult.Reject(19);
            if (operation == InventoryLockOperation.EquipmentLock)
            {
                if (core.Kind == ItemKind.Creature && core.SealFlag != 0)
                    return InventoryResult.Reject(19);
                var template = catalog.Get(core.ItemId);
                if (template.EquipmentSlot == 13 && template.AttachType.Contains("[trade delete]", StringComparison.OrdinalIgnoreCase))
                    return InventoryResult.Reject(17);
                if (core.EquipmentLockId != 0)
                    return InventoryResult.Reject(19);
                var used = before.Items.Select(value => value.Core.EquipmentLockId).ToHashSet();
                int id = Enumerable.Range(1, byte.MaxValue).FirstOrDefault(id => !used.Contains((byte)id));
                if (id == 0)
                    return InventoryResult.Reject(22);
                core = core with { EquipmentLockId = (byte)id };
            }
            else
            {
                if (core.EquipmentLockId == 0)
                    return InventoryResult.Reject(19);
                // 当前策略立即解锁；取消解锁仅确认仍锁定的实例，不重新锁住已解锁物品。
                if (operation == InventoryLockOperation.EquipmentUnlock)
                    core = core with { EquipmentLockId = 0 };
            }
        }

        var after = before with { Items = before.Items.Replace(item, item with { Core = core }) };
        return new(true, 0, new(before, after, operation switch
        {
            InventoryLockOperation.SortLock => "sort_lock",
            InventoryLockOperation.SortUnlock => "sort_unlock",
            InventoryLockOperation.EquipmentLock => "equipment_lock",
            InventoryLockOperation.EquipmentUnlock => "equipment_unlock",
            _ => "equipment_unlock_cancel"
        }));
    }
}
