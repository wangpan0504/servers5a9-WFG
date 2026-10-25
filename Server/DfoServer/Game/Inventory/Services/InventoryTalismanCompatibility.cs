using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryTalismanCompatibility
{
    public static bool AcceptsKind(ItemKind stored, ItemKind current) => stored == current ||
        stored == ItemKind.Equipment && current == ItemKind.Talisman ||
        stored == ItemKind.Consumable && current == ItemKind.Rune;

    public static ItemCore Normalize(ItemCore core, ItemTemplate template)
    {
        if (core.ItemId != template.Id || !AcceptsKind(core.Kind, template.Kind))
            throw new InvalidDataException("物品模板与保存分类不兼容");
        return core.Kind == template.Kind ? core : core with { Kind = template.Kind };
    }

    // 资源确认后的历史分类迁移沿用库存提交；读取原快照不改值，提交仍能复验完整原实例。
    public static InventoryResult Plan(Character character, ItemCatalog? catalog)
    {
        var before = character.Inventory;
        if (catalog is null)
            return new(true, 0, new(before, before, "talisman_compatibility"));
        var planned = before.Items.ToDictionary(item => item.Position);
        foreach (var item in before.Items)
        {
            if (item.Core.Kind is not (ItemKind.Equipment or ItemKind.Consumable))
                continue;
            ItemTemplate template;
            try { template = catalog.Get(item.Core.ItemId); }
            catch (InvalidDataException) { continue; }
            if (template.Kind is not (ItemKind.Talisman or ItemKind.Rune))
                continue;
            var core = Normalize(item.Core, template);
            core.Validate();
            bool cargo = item.Position.List is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;
            if (!cargo && item.Position.List != InventoryListType.Main)
                throw new InvalidDataException("历史护石或符文位于不支持的容器");
            planned.Remove(item.Position);
            int remaining = core.Count;
            bool first = true;
            while (remaining > 0)
            {
                InventorySlot? target = first && cargo ? item.Position : FindFree(template.Kind,
                    cargo ? item.Position.List : ItemSlotBoundService.List(template.Kind));
                if (target is null)
                    // 不足时不产出部分迁移，更不能删除或截断旧资产。
                    return InventoryResult.Reject(17) with { RejectReason = "talisman_compatibility_capacity" };
                int count = Math.Min(remaining, template.StackLimit);
                var part = core.IsStackable ? core with { Value = count } : core;
                planned.Add(target.Value, item with { Uid = first ? item.Uid : 0, Position = target.Value, Core = part });
                first = false;
                remaining -= count;
            }
        }
        var after = before with { Items = planned.Values.OrderBy(item => item.Position.List)
            .ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        return new(true, 0, new(before, after, "talisman_compatibility", new(character)));

        InventorySlot? FindFree(ItemKind kind, InventoryListType list)
        {
            var (start, end) = list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo
                ? ((short)0, checked((short)((list == InventoryListType.PersonalCargo ? before.CargoCapacity : before.AccountCargo.Capacity) - 1)))
                : ItemSlotBoundService.Range(kind, before);
            for (short slot = start; slot <= end; slot++)
            {
                var position = new InventorySlot(list, slot);
                if (!planned.ContainsKey(position))
                    return position;
            }
            return null;
        }
    }
}
