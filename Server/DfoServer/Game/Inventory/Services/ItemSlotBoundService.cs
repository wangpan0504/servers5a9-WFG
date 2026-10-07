using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class ItemSlotBoundService
{
    public const ushort AvatarCapacity = 210;
    public const short GuildMedalEquipmentSlot = 32;
    public static InventoryListType List(ItemKind kind) => kind switch
    {
        ItemKind.Avatar => InventoryListType.Avatar,
        ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.CreatureConsumable => InventoryListType.Pet,
        ItemKind.GuildMedal or ItemKind.GuardianGem => InventoryListType.GuildMedal,
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
        InventoryListType.PersonalCargo => slot.Index >= 0 && slot.Index < inventory.CargoCapacity && CargoKind(kind),
        InventoryListType.AccountCargo => slot.Index >= 0 && slot.Index < inventory.AccountCargo.Capacity && CargoKind(kind),
        _ => Allows(inventory.Expansion, slot, kind)
    };
    static bool CargoKind(ItemKind kind) => kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or
        ItemKind.Profession or ItemKind.GuildMedal or ItemKind.GuardianGem;
    public static (short Start, short End) Range(ItemKind kind, ushort expansion)
    {
        if (kind == ItemKind.Avatar) return (0, AvatarCapacity - 1);
        if (kind == ItemKind.Creature) return (0, 139);
        if (kind == ItemKind.CreatureEquipment) return (140, 188);
        if (kind == ItemKind.CreatureConsumable) return (189, 239);
        if (kind == ItemKind.GuildMedal) return (0, 48);
        if (kind == ItemKind.GuardianGem) return (49, 97);
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
        if (slot.List == InventoryListType.Equipment)
            return kind switch
            {
                ItemKind.Equipment => slot.Index is >= 12 and <= 24,
                ItemKind.Avatar => slot.Index is >= 0 and <= 11,
                ItemKind.Creature => slot.Index == 26,
                ItemKind.CreatureEquipment => slot.Index is >= 27 and <= 29,
                ItemKind.GuildMedal => slot.Index == GuildMedalEquipmentSlot,
                _ => false
            };
        if (slot.List == InventoryListType.Main && slot.Index is >= 3 and <= 8)
            // 快捷栏是真实物品槽；勋章、守护珠也可存放，发放仍选择各自页面。
            return kind is ItemKind.Equipment or ItemKind.Consumable or ItemKind.Material or ItemKind.Quest or
                ItemKind.Profession or ItemKind.GuildMedal or ItemKind.GuardianGem;
        if (!Enum.IsDefined(kind) || slot.List != List(kind))
            return false;
        var range = Range(kind, expansion);
        return slot.Index >= range.Start && slot.Index <= range.End;
    }
}
