using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class AntiEvilUpgradeTests
{
    static void ProviderCore()
    {
        const int seconds = 2_000_000_000;
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        ItemTemplate Template(int id, ItemKind kind, byte slot, int antiEvil, ushort durability = 0) =>
            new(id, kind, slot, 1, durability, 1, 0, 0, 1, "", ushort.MaxValue, 0, "AntiEvil") { AntiEvil = antiEvil };
        ItemTemplate[] templates = [Template(1, ItemKind.Equipment, 12, 25),
            Template(2, ItemKind.Equipment, 14, 5, 20) with { UsableExpired = true },
            Template(3, ItemKind.Equipment, 22, 30), Template(4, ItemKind.Equipment, 13, 8),
            Template(5, ItemKind.Creature, 26, 9), Template(6, ItemKind.Avatar, 9, 10)];
        items.PrefillTemplates(templates.Select(template => KeyValuePair.Create(template.Id, template)));
        var upgrades = new AntiEvilUpgradeCatalog(path => path == "etc/upgrade_separate.etc"
            ? ForgeFixture() : Fixture(firstRarity: path == "etc/amplifyupgrade.etc" ? 3f : 1f));
        var provider = new CharacterAntiEvil(items, upgrades, new RecoveryConsumableCatalog([]),
            new FixedTime(DateTimeOffset.FromUnixTimeSeconds(seconds)));
        InventoryItem Equipped(int id, short? slot = null) => new(id,
            new(InventoryListType.Equipment, slot ?? templates[id - 1].EquipmentSlot),
            new(id, templates[id - 1].Kind, 0) { Durability = templates[id - 1].Durability });
        Character Role(params InventoryItem[] equipment) => new(1, 0, "AntiBoundary", 0, new(1, 0, 0, 0), null)
            { Level = 95, Job = 0, EquipmentSlotFlags = 40, Inventory = new([.. equipment]) };

        var weapon = Equipped(1);
        That(provider.Compute(Role(weapon with { Core = weapon.Core with { ExpireTime = seconds } })) == 25,
            "原生截止秒等于当前秒仍有效，只有截止秒严格小于当前秒才去掉装备抗魔");
        var expiredUsable = Equipped(2);
        That(provider.Compute(Role(expiredUsable with { Core = expiredUsable.Core with { ExpireTime = 1 } })) == 5 &&
            provider.Compute(Role(weapon with { Core = weapon.Core with { ExpireTime = seconds - 1 } })) == 0,
            "过期可用标签保留真实属性；未标记的普通过期装备排除");
        That(provider.Compute(Role(weapon)) == 25 &&
            provider.Compute(Role(expiredUsable with { Core = expiredUsable.Core with { Durability = 0 } })) == 0,
            "无最大耐久的零当前耐久装备有效；过期可用标记不绕过破损过滤");
        That(provider.Compute(Role(Equipped(4), Equipped(5), Equipped(6), Equipped(5, 33), Equipped(6, 11))) == 27,
            "称号、宠物、光环根属性各计一次，两个已解锁外观槽不重复合入");
        var support = Role(Equipped(3));
        That(provider.Compute(support) == 0 && provider.Compute(support with { EquipmentSlotFlags = 41 }) == 30,
            "辅助装备槽未解锁不产生属性，真实解锁后计入");
        var upgraded = weapon with { Core = weapon.Core with { Attr = 10, GenuineUpgrade = 1 } };
        That(provider.Compute(Role(upgraded)) == 52 &&
            provider.Compute(Role(upgraded with { Core = upgraded.Core with { AmplifyType = 128 } })) == 105 &&
            provider.Compute(Role(expiredUsable with { Core = expiredUsable.Core with { GenuineUpgrade = 8 } })) == 5,
            "非零增幅类型128选择增幅目录，强化与锻造取最大值且非武器不取锻造表");
    }

    static void MemberSetListProviderCore()
    {
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        ItemTemplate Template(int id, byte slot, params int[] setIds) =>
            new(id, ItemKind.Equipment, slot, 1, 0, 1, 0, 0, 1, "", ushort.MaxValue, 0, "SetMember")
                { PartSetIds = System.Collections.Immutable.ImmutableArray.CreateRange(setIds) };
        ItemTemplate[] templates = [Template(1, 12, 30123, 30125, 30123, -1, 0),
            Template(2, 14, 30123), Template(3, 16, 30125)];
        items.PrefillTemplates(templates.Select(template => KeyValuePair.Create(template.Id, template)));
        string[] strings = ["[equipment part set]", "[/equipment part set]", "partset/first.equ", "partset/second.equ",
            "[piece set ability]", "[/piece set ability]", "[anti evil]"];
        PvfTokenDocument Registry() => new([
            new(3, 0), new(0, 30123), new(6, 2), new(0, 0), new(3, 1),
            new(3, 0), new(0, 30125), new(6, 3), new(0, 0), new(3, 1)], index => strings[index]);
        PvfTokenDocument Tier(int amount) => new([
            new(3, 4), new(0, 2), new(3, 6), new(0, amount), new(3, 5)], index => strings[index]);
        var sets = new AntiEvilSetCatalog(path => path switch
        {
            "etc/equipmentpartset.etc" => Registry(),
            "equipment/partset/first.equ" => Tier(11),
            "equipment/partset/second.equ" => Tier(17),
            _ => throw new InvalidOperationException("未知套装候选资源:" + path)
        }, index => strings[index]);
        var upgrades = new AntiEvilUpgradeCatalog(path => path == "etc/upgrade_separate.etc" ? ForgeFixture() : Fixture());
        var provider = new CharacterAntiEvil(items, upgrades, new RecoveryConsumableCatalog([]),
            new FixedTime(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000)), sets: sets);
        InventoryItem Equipped(int id) => new(id, new(InventoryListType.Equipment, templates[id - 1].EquipmentSlot),
            new(id, ItemKind.Equipment, 0));
        Character Role(params InventoryItem[] worn) => new(1, 0, "SetLists", 0, new(1, 0, 0, 0), null)
            { Level = 95, Job = 0, Inventory = new([.. worn]) };
        That(provider.Compute(Role(Equipped(1))) == 0,
            "同一件多成员列表重复30123不能伪造套装两件门槛，-1与0不查询目录");
        That(provider.Compute(Role(Equipped(1), Equipped(2), Equipped(3))) == 28,
            "一件同时属于两个正套装，各搭配另一件后分别激活11与17，不丢失列表第二项");
        That(provider.Compute(Role(Equipped(1) with { Core = Equipped(1).Core with { ExpireTime = 1 } }, Equipped(2), Equipped(3))) == 0,
            "共享多成员装备过期后先被原过滤排除，两个套装都不虚构第二件");
    }
}
