using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class QuickSlotEquipmentRules
{
    public static bool IsQuickSlot(InventorySlot slot) => slot.List == InventoryListType.Main &&
        slot.Index is >= ItemSlotBoundService.QuickSlotStart and <= ItemSlotBoundService.QuickSlotEnd;

    public static bool IsActive(InventoryItem item, ItemTemplate template, long now) =>
        IsQuickSlot(item.Position) && item.Core.Kind == ItemKind.Equipment && template.IsQuickSlotEquipment &&
        template.QuickSlotEquipment is not null && !item.IsExpired(now) &&
        // ARAD 02949C50：上限非零且当前耐久为0才损坏；永久纹章不检查品级Value。
        (template.Durability == 0 || item.Core.Durability > 0);

    public static bool Allows(Character character, InventorySlot target, ItemTemplate template,
        ItemCatalog items, InventorySlot? movingSource)
    {
        if (!IsQuickSlot(target) || !template.IsQuickSlotEquipment) return true;
        // 029E6470：符咒从5级起装入快捷栏；在背包或仓库保存它不属于装备行为。
        if (character.Level < 5 || !template.AllowsUse(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, template)) return false;
        foreach (var item in character.Inventory.Items)
            if (IsQuickSlot(item.Position) && item.Position != target && item.Position != movingSource &&
                item.Core.Kind == ItemKind.Equipment && items.Get(item.Core.ItemId).IsQuickSlotEquipment)
                return false;
        return true;
    }
}
