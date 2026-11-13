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
        // 护石窗口的整理分类是客户端页面号而不是物品类别：18=符文页70–209，19=护石页0–69
        // （实机 2026-10-25 校正：护石页发19、符文页发18，此前按物品类别错配成反向）。
        // 已装符文210–215绑定装备槽，不属于任何背包页，整理不参与。
        (short Start, short End)? page = list != InventoryListType.Talisman ? null : (byte)kind switch
        {
            18 or 14 => ((short)70, (short)209),
            19 or 11 => ((short)0, (short)69),
            _ => null
        };
        bool runePage = page?.Start == 70;
        if (list == InventoryListType.Talisman ? page is null :
            !cargo && (!Enum.IsDefined(kind) || ItemSlotBoundService.List(kind) != list) ||
            cargo && (list == InventoryListType.PersonalCargo ? before.CargoCapacity : before.AccountCargo.Capacity) == 0 ||
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
        // 仓库页码不是物品类型；先分组，再复用各物品栏页面的排序规则。
        IEnumerable<InventoryItem> ordered = cargo
            ? movable.GroupBy(item => item.Core.Kind).OrderBy(group => group.Key).SelectMany(group => Order(group, group.Key))
            : Order(movable, kind);
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

        IOrderedEnumerable<InventoryItem> Order(IEnumerable<InventoryItem> input, ItemKind groupKind)
        {
            bool groupRunePage = runePage || cargo && groupKind == ItemKind.Rune;
            bool groupSpecialPage = page is not null || cargo && groupKind is (ItemKind.Rune or ItemKind.Talisman);
            // 不支持的条件回退页面默认；时装默认稀有度需读模板，其余默认只用实例字段。
            // 护石窗口整理菜单与背包同一套编号：1=按品级；其余回页面默认。
            byte algorithm = groupSpecialPage ? condition == 1 ? (byte)1 : (byte)7
                : list == InventoryListType.Avatar ? condition == 3 ? (byte)3 : (byte)1 : list != InventoryListType.Main && !cargo ? (byte)0 : groupKind switch
            {
                ItemKind.Equipment when condition is 1 or 2 or 3 => condition,
                ItemKind.Consumable when condition is 3 or 4 => condition,
                // 主背包材料页“按类型”发6；旧3仍兼容，仓库和副职业不套用新编号。
                ItemKind.Material when list == InventoryListType.Main && condition == 6 => (byte)3,
                ItemKind.Material or ItemKind.Profession when condition == 3 => condition,
                _ => (byte)0
            };
            // 客户端 029CE630(condition=0) 经 029CB470 只检查空槽后是否仍有物品：
            // 背包页按栏位整理只向前紧凑填充空槽、保持原有槽位先后，仓库默认整理才按类别与 itemId 排序。
            if (!cargo && algorithm == 0)
                return input.OrderBy(item => item.Position.Index);
            var sorted = algorithm switch
            {
                // 符文品级来自资源，不使用模板稀有度。
                1 when groupRunePage => input.OrderByDescending(item => catalog.Rune(item.Core.ItemId)?.Grade ?? 0),
                1 => input.OrderByDescending(item => RarityRank(catalog.Get(item.Core.ItemId).Rarity)),
                2 => input.OrderByDescending(item => catalog.Get(item.Core.ItemId).MinimumLevel),
                3 when groupKind is ItemKind.Equipment or ItemKind.Avatar => input.OrderBy(item => catalog.Get(item.Core.ItemId).EquipmentSlot),
                3 => input.OrderBy(item => catalog.Get(item.Core.ItemId).StackableType, StringComparer.OrdinalIgnoreCase),
                4 => input.OrderBy(item => item.ExpireTime <= 0 ? int.MaxValue : item.ExpireTime),
                7 when groupRunePage => input.OrderBy(item => catalog.Rune(item.Core.ItemId) is { } rune ? (int)rune.Camp : 0)
                    .ThenByDescending(item => catalog.Rune(item.Core.ItemId)?.Grade ?? 0),
                7 => input.OrderByDescending(item => catalog.Get(item.Core.ItemId).MinimumLevel),
                _ => input.OrderBy(item => item.Core.Kind)
            };
            return sorted.ThenBy(item => item.Core.Kind).ThenBy(item => item.Core.ItemId).ThenBy(item => item.Position.Index);
        }

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
