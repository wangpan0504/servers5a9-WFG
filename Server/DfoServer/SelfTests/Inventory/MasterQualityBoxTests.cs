using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MasterQualityBoxTests
{
    public static void RunCore()
    {
        int before = Count;
        var epic = EquipmentQualityBox.Parse(10150863, new PvfDocument("""
            [rarity]
            4
            [stackable type]
            `[etc]` 0
            [variable need material]
            [material count]
            1
            [materials]
            15 20 897 20
            [/materials]
            [/variable need material]
            """));
        That(epic is { MinimumPercent: 100, MaximumPercent: 100, RequiredRarity: 4, RejectTitle: true } && epic.Roll(_ => 0) == 999999998,
            "史诗大师箱固定最高品级种子");
        That(EquipmentQualityBox.Parse(1, new PvfDocument("[stackable type]\n`[etc]` 0\n[variable need material]\n[material count]\n1\n[materials]\n15 1 16 1\n[/materials]\n[/variable need material]\n")) is null,
            "不是15/897成对材料的声明不当成品级箱");
        var weapon = new ItemTemplate(100, ItemKind.Equipment, 12, 1, 30, 1, 0, 4, 0, "[sealing]", ushort.MaxValue, 0, "史诗武器");
        var rare = weapon with { Id = 101, Rarity = 2, Name = "稀有武器" };
        var title = weapon with { Id = 102, EquipmentSlot = 13, Name = "史诗称号" };
        var box = new ItemTemplate(10150863, ItemKind.Consumable, 0, 1, 0, 99, 0, 4, 0, "[trade]", ushort.MaxValue, 0, "大师级装备品级调整箱 [史诗]")
            { QualityBox = epic };
        That(epic!.Allows(weapon) && !epic.Allows(rare) && !epic.Allows(title) && !epic.Allows(box),
            "只接受同稀有度的武器到耳环，称号和其他道具拒绝");
        var options = new RandomOptionCatalog(path => path switch
        {
            "etc/randomoption/randomoption.lst" => "1 `options/one.etc`",
            "etc/randomoption/optionquantity.etc" => "[common]\n1 10\n[/common]\n[unique]\n1 2\n[/unique]",
            "etc/randomoption/optiongroupselection.etc" => "[choose option group]\n2 -1 `lt shoes` 1 3 1000 1000 1000 0 0 1000\n[/choose option group]",
            "etc/randomoption/optiongrouping.etc" => "[option group]\n1000 1 10\n[/option group]",
            "etc/randomoption/randomizedoptionoverall2.etc" => "[break seal cost]\n2 1 1 0\n[/break seal cost]",
            _ => throw new InvalidDataException(path)
        }, new Dictionary<int, string>());
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates([new(weapon.Id, weapon), new(rare.Id, rare), new(title.Id, title), new(box.Id, box)]);
        var service = new InventoryRandomOptionService(items, options, _ => 0);
        var core = new ItemCore(weapon.Id, ItemKind.Equipment, 12) { Random0 = new(70, 3, 4), Random1 = new(180, 5, 6), EnchantCardId = 9, Attr = 4 };
        Character Role(ItemCore target) => new(1, 0, "MasterBox", 0, new(1, 0, 1, 1), null)
        {
            Level = 90,
            Inventory = new([new(8, new(InventoryListType.Main, 4), target), new(9, new(InventoryListType.Main, 6), new(box.Id, ItemKind.Consumable, 1))])
        };
        var accepted = service.ResetQuality(Role(core), new(4, weapon.Id, 6, 0, false));
        That(accepted.Accepted && accepted.Plan!.After.Find(new(InventoryListType.Main, 4))!.Core == core with { Value = 999999998 } &&
            accepted.Plan.After.Find(new(InventoryListType.Main, 6)) is null, "史诗武器品级取最高，魔法封印、附魔和强化保持，箱子消耗");
        That(!service.ResetQuality(Role(core with { ItemId = rare.Id }), new(4, rare.Id, 6, 0, false)).Accepted &&
            !service.ResetQuality(Role(core with { ItemId = title.Id }), new(4, title.Id, 6, 0, false)).Accepted,
            "稀有装备和史诗称号仍拒绝且不消耗箱子");
        Console.WriteLine($"PASS MasterQualityBoxTests core ({Count - before} checks)");
    }
}
