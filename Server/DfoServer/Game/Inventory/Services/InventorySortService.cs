using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventorySortService
{
    public static InventoryResult Plan(InventorySnapshot before, ItemKind kind, byte condition, ItemCatalog catalog, long utcSeconds,
        InventoryListType list = InventoryListType.Main)
    {
        bool cargo = list is InventoryListType.PersonalCargo or InventoryListType.AccountCargo;
        if (!cargo && (!Enum.IsDefined(kind) || ItemSlotBoundService.List(kind) != list) ||
            list == InventoryListType.PersonalCargo && (byte)kind != 11)
            return InventoryResult.Reject();
        var range = !cargo ? ItemSlotBoundService.Range(kind, before.Expansion) :
            (Start: (short)0, End: (short)((list == InventoryListType.PersonalCargo ? before.CargoCapacity : before.AccountCargo.Capacity) - 1));
        var planned = before.Items.ToDictionary(item => item.Position);
        // 到期先于排序锁处理；安全锁只保护资产操作，不固定整理位置。
        foreach (var item in before.Items.Where(item => InPage(item) && item.IsExpired(utcSeconds)))
            planned.Remove(item.Position);
        var movable = planned.Values.Where(item => InPage(item) && item.Core.SortLockFlag == 0)
            .OrderBy(item => item.Position.Index).ToArray();
        foreach (var item in movable)
            planned.Remove(item.Position);
        IEnumerable<InventoryItem> ordered = movable;
        // 不支持的条件回退页面默认；时装默认稀有度需读模板，其余默认只用实例字段。
        byte algorithm = list == InventoryListType.Avatar ? condition == 3 ? (byte)3 : (byte)1 : list != InventoryListType.Main ? (byte)0 : kind switch
        {
            ItemKind.Equipment when condition is 1 or 2 or 3 => condition,
            ItemKind.Consumable when condition is 3 or 4 => condition,
            ItemKind.Material or ItemKind.Profession when condition == 3 => condition,
            _ => (byte)0
        };
        var sorted = algorithm switch
        {
            1 => ordered.OrderByDescending(item => RarityRank(catalog.Get(item.Core.ItemId).Rarity)),
            2 => ordered.OrderByDescending(item => catalog.Get(item.Core.ItemId).MinimumLevel),
            3 when kind is ItemKind.Equipment or ItemKind.Avatar => ordered.OrderBy(item => catalog.Get(item.Core.ItemId).EquipmentSlot),
            3 => ordered.OrderBy(item => catalog.Get(item.Core.ItemId).StackableType, StringComparer.OrdinalIgnoreCase),
            4 => ordered.OrderBy(item => item.ExpireTime <= 0 ? int.MaxValue : item.ExpireTime),
            _ => ordered.OrderBy(item => item.Core.Kind)
        };
        ordered = sorted.ThenBy(item => item.Core.Kind).ThenBy(item => item.Core.ItemId).ThenBy(item => item.Position.Index);
        short slot = range.Start;
        foreach (var item in ordered)
        {
            while (planned.ContainsKey(new(list, slot)))
                slot++;
            var position = new InventorySlot(list, slot++);
            planned.Add(position, item with { Position = position });
        }

        var after = before with { Items = planned.Values.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate();
        return new(true, 0, new(before, after, "sort_item"));

        bool InPage(InventoryItem item) => item.Position.List == list &&
            item.Position.Index >= range.Start && item.Position.Index <= range.End;
    }

    internal static int RarityRank(int rarity) => rarity switch
    {
        4 => 6,
        6 => 5,
        3 => 4,
        5 => 3,
        _ => Math.Max(0, rarity)
    };
}
