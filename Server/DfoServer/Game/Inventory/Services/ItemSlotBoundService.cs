using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class ItemSlotBoundService
{
    public const short QuickSlotStart = 3;
    public const short QuickSlotEnd = 8;
    public const ushort AvatarCapacity = 210;
    public const short AccountCommonCapacity = 50;
    public const short GuildMedalEquipmentSlot = 32;
    public const short TalismanEquipmentStart = 34, TalismanEquipmentEnd = 35;
    public const short EquippedRuneStart = 210, EquippedRuneEnd = 215;
    public static InventoryListType List(ItemKind kind) => kind switch
    {
        ItemKind.Avatar => InventoryListType.Avatar,
        ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.CreatureConsumable => InventoryListType.Pet,
        ItemKind.GuildMedal or ItemKind.GuardianGem => InventoryListType.GuildMedal,
        ItemKind.Talisman or ItemKind.Rune => InventoryListType.Talisman,
        _ => InventoryListType.Main
    };
    public static short? CurrencySlot(CurrencyKind kind) => kind switch
    {
        >= CurrencyKind.Gold and <= CurrencyKind.WinPoint => (short)kind,
        >= CurrencyKind.BlackCube and <= CurrencyKind.EpicSoul => (short)(354 + kind - CurrencyKind.BlackCube),
        _ => null
    };

    public static bool Allows(InventorySnapshot inventory, InventorySlot slot, ItemKind kind) => slot.List switch
    {
        InventoryListType.Avatar => kind == ItemKind.Avatar && slot.Index >= 0 && slot.Index < 105 + inventory.AvatarExpansion,
        InventoryListType.PersonalCargo => slot.Index >= 0 && slot.Index < inventory.CargoCapacity && CargoKind(kind),
        InventoryListType.AccountCargo => slot.Index >= 0 && slot.Index < inventory.AccountCargo.Capacity && CargoKind(kind),
        _ => Allows(inventory.Expansion, slot, kind)
    };
    static bool CargoKind(ItemKind kind) => kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or
        ItemKind.Profession or ItemKind.GuildMedal or ItemKind.GuardianGem or ItemKind.Talisman or ItemKind.Rune;
    // 已装载符文是护石孔位的唯一实例，只能由装载/卸除业务移动，通用删除、出售与锁定一律拒绝。
    public static bool IsEquippedRune(InventorySlot slot) =>
        slot.List == InventoryListType.Talisman && slot.Index is >= EquippedRuneStart and <= EquippedRuneEnd;
    public static (short Start, short End) Range(ItemKind kind, InventorySnapshot inventory) =>
        kind == ItemKind.Avatar ? ((short)0, checked((short)(104 + inventory.AvatarExpansion))) : Range(kind, inventory.Expansion);
    public static (short Start, short End) Range(ItemKind kind, ushort expansion)
    {
        if (kind == ItemKind.Avatar) return (0, AvatarCapacity - 1);
        if (kind == ItemKind.Creature) return (0, 139);
        if (kind == ItemKind.CreatureEquipment) return (140, 188);
        if (kind == ItemKind.CreatureConsumable) return (189, 239);
        if (kind == ItemKind.GuildMedal) return (0, 48);
        if (kind == ItemKind.GuardianGem) return (49, 97);
        if (kind == ItemKind.Talisman) return (0, 69);
        if (kind == ItemKind.Rune) return (70, 209);
        short start = kind switch
        {
            ItemKind.Equipment => 9,
            ItemKind.Consumable => 65,
            ItemKind.Material => 121,
            ItemKind.Quest => 177,
            ItemKind.Profession => 233,
            ItemKind.Emblem => 289,
            _ => throw new InvalidDataException("未知背包分类")
        };
        return (start, checked((short)(start + (kind == ItemKind.Emblem ? 62 : 31 + expansion))));
    }

    public static bool Allows(ushort expansion, InventorySlot slot, ItemKind kind)
    {
        if (slot.List == InventoryListType.AccountCommon)
            return slot.Index is >= 0 and < AccountCommonCapacity && kind is
                ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or ItemKind.CreatureConsumable or
                ItemKind.Emblem or ItemKind.Profession or ItemKind.GuardianGem or ItemKind.Rune;
        // 实际分类容量与允许的称号模板由称号簿用例再校验。
        if (slot.List is >= InventoryListType.TitleGeneral and <= InventoryListType.TitleEvent)
            return kind == ItemKind.Equipment && slot.Index is >= 0 and < 256;
        if (slot.List == InventoryListType.Equipment)
            return kind switch
            {
                ItemKind.Equipment => slot.Index is >= 12 and <= 25 or 30,
                ItemKind.Avatar => slot.Index is >= 0 and <= 11,
                ItemKind.Creature => slot.Index is 26 or 33,
                ItemKind.CreatureEquipment => slot.Index is >= 27 and <= 29,
                ItemKind.GuildMedal => slot.Index == GuildMedalEquipmentSlot,
                ItemKind.Talisman => slot.Index is TalismanEquipmentStart or TalismanEquipmentEnd,
                _ => false
            };
        if (slot.List == InventoryListType.Main && slot.Index is >= QuickSlotStart and <= QuickSlotEnd)
            // 快捷栏是真实物品槽；自动入包顺序由对应发放或拾取策略决定。
            return kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or
                ItemKind.Profession or ItemKind.GuildMedal or ItemKind.GuardianGem;
        if (!Enum.IsDefined(kind) || slot.List != List(kind))
            return false;
        // 装载符文也是唯一库存实例，自动发放只使用Range中的符文背包格。
        if (kind == ItemKind.Rune && slot.Index is >= EquippedRuneStart and <= EquippedRuneEnd)
            return true;
        var range = Range(kind, expansion);
        return slot.Index >= range.Start && slot.Index <= range.End;
    }
}
