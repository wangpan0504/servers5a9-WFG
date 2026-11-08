using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class EpicPieceBonusDropPolicy
{
    public static int Capture(Character character, EpicPieceBonusDropCatalog catalog, long now, string module)
    {
        if (catalog.MaximumBonus == 0) return 0;
        int bonus = 0;
        var held = new HashSet<int>();
        foreach (var item in character.Inventory.Items)
        {
            if (catalog.Equipment.TryGetValue(item.Core.ItemId, out var worn))
            {
                var template = worn.Template;
                if (item.Position.List != InventoryListType.Equipment || item.Position.Index is 11 or 33 ||
                    item.Position.Index != template.EquipmentSlot || !Active(item, template, now, module)) continue;
                if (template.CreatureEgg || template.Kind == ItemKind.Creature && item.Creature?.Satiety is not > 0) continue;
                bonus = checked(bonus + worn.Count);
            }
            else if (catalog.Stackable.TryGetValue(item.Core.ItemId, out var carried))
            {
                // 当前丹石在材料栏持有生效；仓库、邮件、快捷栏及同编号的额外格子不提供另一份资格。
                var template = carried.Template;
                if (item.Position.List != InventoryListType.Main || ItemSlotBoundService.List(template.Kind) != InventoryListType.Main || item.Core.Count <= 0 ||
                    !Active(item, template, now, module)) continue;
                var range = ItemSlotBoundService.Range(template.Kind, character.Inventory);
                if (item.Position.Index < range.Start || item.Position.Index > range.End || !held.Add(template.Id)) continue;
                bonus = checked(bonus + carried.Count);
            }
        }
        return bonus;
    }

    static bool Active(InventoryItem item, ItemTemplate template, long now, string module) =>
        item.Core.Kind == template.Kind && !template.UseForbidden && (!item.IsExpired(now) || template.UsableExpired) &&
        !template.ExperienceForbiddenModules.Contains(module);
}
