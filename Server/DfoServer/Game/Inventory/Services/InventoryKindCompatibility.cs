using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryKindCompatibility
{
    public static bool AcceptsKind(ItemKind stored, ItemTemplate template) => stored == template.Kind ||
        stored == ItemKind.Equipment && template.Kind == ItemKind.Talisman ||
        stored == ItemKind.Consumable && template.Kind == ItemKind.Rune ||
        // 仅兼容已确认的历史公会硬币，不允许任意材料转成任务物品。
        stored == ItemKind.Material && template.Id == GameWorld.Guilds.GuildNpcCatalog.MemberCoinItem && template.Kind == ItemKind.Quest;

    public static ItemCore Normalize(ItemCore core, ItemTemplate template)
    {
        if (core.ItemId != template.Id || !AcceptsKind(core.Kind, template))
            throw new InvalidDataException("物品模板与保存分类不兼容");
        return core.Kind == template.Kind ? core : core with { Kind = template.Kind };
    }

    // 资源确认后的历史分类迁移沿用库存提交；读取原快照不改值，提交仍能复验完整原实例。
    public static InventoryResult Plan(Character character, ItemCatalog? catalog)
    {
        var before = character.Inventory;
        if (catalog is null)
            return new(true, 0, new(before, before, "item_kind_compatibility"));
        var planned = before.Items.ToDictionary(item => item.Position);
        foreach (var item in before.Items)
        {
            if (item.Core.Kind is not (ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material))
                continue;
            ItemTemplate template;
            try { template = catalog.Get(item.Core.ItemId); }
            catch (InvalidDataException) { continue; }
            if (item.Core.Kind == template.Kind || !AcceptsKind(item.Core.Kind, template))
                continue;
            bool cargo = item.Position.List is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;
            // 任务物品不能存入仓库；旧材料币先保留仓库原实例，取出时才归一，不能选角即接管账号资产。
            if (cargo && !ItemSlotBoundService.Allows(before, item.Position, template.Kind))
                continue;
            var core = Normalize(item.Core, template);
            core.Validate();
            if (!cargo && item.Position.List != InventoryListType.Main)
                throw new InvalidDataException("历史分类物品位于不支持的容器");
            planned.Remove(item.Position);
            int remaining = core.Count;
            bool first = true;
            while (remaining > 0)
            {
                InventorySlot? target = first && cargo ? item.Position : FindFree(template.Kind,
                    cargo ? item.Position.List : ItemSlotBoundService.List(template.Kind));
                if (target is null)
                    // 不足时不产出部分迁移，更不能删除或截断旧资产。
                    return InventoryResult.Reject(17) with { RejectReason = "item_kind_compatibility_capacity" };
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
        return new(true, 0, new(before, after, "item_kind_compatibility", new(character)));

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
