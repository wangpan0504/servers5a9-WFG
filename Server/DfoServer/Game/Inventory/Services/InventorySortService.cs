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
        // 护石窗口的整理分类是客户端页面号而不是物品类别：18=护石页0–69，19=符文页70–209。
        // 已装符文210–215绑定装备槽，不属于任何背包页，整理不参与。
        (short Start, short End)? page = list != InventoryListType.Talisman ? null : (byte)kind switch
        {
            18 or 11 => ((short)0, (short)69),
            19 or 14 => ((short)70, (short)209),
            _ => null
        };
        if (list == InventoryListType.Talisman ? page is null :
            !cargo && (!Enum.IsDefined(kind) || ItemSlotBoundService.List(kind) != list) ||
            list == InventoryListType.PersonalCargo && (byte)kind is not (11 or 20))
            return InventoryResult.Reject();
        var range = page ?? (!cargo ? ItemSlotBoundService.Range(kind, before) :
            (Start: (short)0, End: (short)((list == InventoryListType.PersonalCargo ? before.CargoCapacity : before.AccountCargo.Capacity) - 1)));
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
        // 护石窗口整理菜单与背包同一套编号：1=按品级（稀有度/符文品级降序）；其余（含菜单里的「按组织」
        // 与默认项）回页面默认，护石页按等级降序、符文页按阵营归组再按品级降序。
        byte algorithm = page is not null ? condition == 1 ? (byte)1 : (byte)7
            : list == InventoryListType.Avatar ? condition == 3 ? (byte)3 : (byte)1 : list != InventoryListType.Main ? (byte)0 : kind switch
        {
            ItemKind.Equipment when condition is 1 or 2 or 3 => condition,
            ItemKind.Consumable when condition is 3 or 4 => condition,
            ItemKind.Material or ItemKind.Profession when condition == 3 => condition,
            _ => (byte)0
        };
        var sorted = algorithm switch
        {
            // 符文品级来自资源 `[rune option]` 而不是模板稀有度，按品级整理必须读符文定义。
            1 when (byte)kind is 19 or 14 => ordered.OrderByDescending(item => catalog.Rune(item.Core.ItemId)?.Grade ?? 0),
            1 => ordered.OrderByDescending(item => RarityRank(catalog.Get(item.Core.ItemId).Rarity)),
            2 => ordered.OrderByDescending(item => catalog.Get(item.Core.ItemId).MinimumLevel),
            3 when kind is ItemKind.Equipment or ItemKind.Avatar => ordered.OrderBy(item => catalog.Get(item.Core.ItemId).EquipmentSlot),
            3 => ordered.OrderBy(item => catalog.Get(item.Core.ItemId).StackableType, StringComparer.OrdinalIgnoreCase),
            4 => ordered.OrderBy(item => item.ExpireTime <= 0 ? int.MaxValue : item.ExpireTime),
            7 when (byte)kind is 19 or 14 => ordered.OrderBy(item => catalog.Rune(item.Core.ItemId) is { } rune ? (int)rune.Camp : 0)
                .ThenByDescending(item => catalog.Rune(item.Core.ItemId)?.Grade ?? 0),
            7 => ordered.OrderByDescending(item => catalog.Get(item.Core.ItemId).MinimumLevel),
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
