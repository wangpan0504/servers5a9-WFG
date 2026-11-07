using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 本服按实际疲劳扣减消耗能量；资格在锁外读取资源，写事务只使用本次冻结的物品事实。
internal static class QuickSlotEquipmentEnergy
{
    public static InventoryItem? Capture(Character character, ItemCatalog? items, long now)
    {
        if (items is null) return null;
        foreach (var item in character.Inventory.Items.Where(item => QuickSlotEquipmentRules.IsQuickSlot(item.Position) &&
            item.Core.Kind == ItemKind.Equipment).OrderBy(item => item.Position.Index))
        {
            if (!items.TryGet(item.Core.ItemId, out var template) || !template.IsQuickSlotEquipment) continue;
            // 与经验资格相同，存量多件只认首件，不用第二件绕过永久型、到期或空能量状态。
            return template.Durability > 0 && QuickSlotEquipmentRules.IsActive(item, template, now) ? item : null;
        }
        return null;
    }

    public static InventoryPlan? Plan(Character expected, InventorySnapshot before, InventoryItem? source, int fatigueLost)
    {
        if (fatigueLost <= 0 || source is null || before.Find(source.Position) is not { } current ||
            current.Uid != source.Uid || current.Core.ItemId != source.Core.ItemId || current.Core.Durability == 0)
            return null;
        var changed = current with { Core = current.Core with
            { Durability = (ushort)Math.Max(0, current.Core.Durability - fatigueLost) } };
        return new(before, before with { Items = before.Items.Replace(current, changed) }, "quick_slot_energy_fatigue",
            new(expected));
    }

    public static ImmutableArray<InventorySlot> ChangedSlots(InventorySnapshot before, InventorySnapshot after) =>
        [.. before.Items.Where(item => QuickSlotEquipmentRules.IsQuickSlot(item.Position) &&
            after.Find(item.Position) is { } current && current.Uid == item.Uid &&
            current.Core.Durability < item.Core.Durability).Select(item => item.Position)];
}
