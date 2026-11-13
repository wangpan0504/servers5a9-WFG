using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class EquipmentRarityBonusPolicy
{
    static bool AttributeSlot(InventorySlot slot) => slot.List == InventoryListType.Equipment && slot.Index is not (11 or 33);

    // 入场资源准备在owner外执行；穿脱入口已装载源与目标，不在死亡/结算锁内解析资源。
    public static void Prepare(Character character, ItemCatalog items)
    {
        foreach (var item in character.Inventory.Items)
            if ((AttributeSlot(item.Position) || QuickSlotEquipmentRules.IsQuickSlot(item.Position)) &&
                !items.TryGetLoaded(item.Core.ItemId, out _)) items.TryGet(item.Core.ItemId, out _);
    }

    public static EquipmentRarityBonus Capture(Character character, ItemCatalog items, long now, string module)
    {
        EquipmentRarityBonus bonus = default;
        var sets = new Dictionary<int, int>();
        bool quickSlotSeen = false;
        foreach (var item in character.Inventory.Items.OrderBy(item => item.Position.Index))
        {
            bool quickSlot = QuickSlotEquipmentRules.IsQuickSlot(item.Position);
            if (!AttributeSlot(item.Position) && !quickSlot) continue;
            if (!items.TryGetLoaded(item.Core.ItemId, out var template))
            {
                if (items.Contains(item.Core.ItemId)) throw new InvalidDataException("装备掉率模板尚未准备");
                continue;
            }
            if (template.Kind != item.Core.Kind) continue;
            if (quickSlot)
            {
                if (!template.IsQuickSlotEquipment || quickSlotSeen) continue;
                quickSlotSeen = true;
                if (character.Level < 5 || !QuickSlotEquipmentRules.IsActive(item, template, now)) continue;
            }
            else if (template.IsQuickSlotEquipment) continue;
            if (template.CreatureEgg || item.IsExpired(now) && !template.UsableExpired ||
                template.ExperienceForbiddenModules.Contains(module)) continue;
            if (EquipmentRarityBonusDefinition.Get(item.Core.ItemId) is { } definition &&
                template.Kind == ItemKind.Equipment && template.EquipmentSlot == definition.Slot &&
                (quickSlot || item.Position.Index == definition.Slot)) bonus = bonus.Add(definition.Bonus);
            if (quickSlot) continue;
            if (template.Kind != ItemKind.Equipment || template.EquipmentSlot != item.Position.Index) continue;
            foreach (int set in template.PartSetIds.Distinct())
                if (!EquipmentRarityBonusDefinition.SetBonus(set).IsEmpty) sets[set] = sets.GetValueOrDefault(set) + 1;
        }
        foreach (var (set, count) in sets)
            if (count >= 3) bonus = bonus.Add(EquipmentRarityBonusDefinition.SetBonus(set));
        return bonus;
    }
}
