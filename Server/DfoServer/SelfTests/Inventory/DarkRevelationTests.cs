using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class DarkRevelationTests
{
    public static void RunCore()
    {
        const string table = """
            [group]
            [key]
            `A`
            [indexes]
            1 2 3 4
            [/indexes]
            [/group]
            [group]
            [key]
            `B`
            [indexes]
            11 12 13 14
            [/indexes]
            [/group]
            [first material]
            200 1
            [/first material]
            [material]
            200 1 201 1
            [/material]
            [enchant condition]
            [slot]
            0
            [equipment]
            [type]
            `[amulet]`
            [list]
            101
            [/list]
            [/equipment]
            [/enchant condition]
            [enchant condition]
            [slot]
            1
            [equipment]
            [type]
            `[support]`
            [list]
            102
            [/list]
            [/equipment]
            [/enchant condition]
            """;
        var catalog = new DarkRevelationCatalog(table, new Dictionary<int, string> { [400] = "weapon.equ" },
            _ => "[custom ability type]\n`dark revelation`\n[custom ability count]\n2\n[upgrade dark revelation]\n1\n");
        var service = new InventoryDarkRevelationService(catalog, new("[piece list]\n50000 50001\n[/piece list]"));
        var source = new InventoryItem(1, new(InventoryListType.Main, 26), new(400, ItemKind.Equipment, 999999998)
        {
            Attr = 15, EnchantCardId = 12345, Extension114 = new(0x77, 0xA0000000)
        });
        var character = new Character(1, 0, "Revelation", 0, new(1, 0, 10, 10), null)
        {
            Level = 95,
            Inventory = new([
                source,
                new(2, new(InventoryListType.Equipment, 19), new(101, ItemKind.Equipment, 999999998)),
                new(3, new(InventoryListType.Equipment, 22), new(102, ItemKind.Equipment, 999999998)),
                new(4, new(InventoryListType.Main, 30), new(200, ItemKind.Material, 5)),
                new(5, new(InventoryListType.Main, 31), new(201, ItemKind.Material, 5))], 24, 600000)
        };
        var first = service.Apply(character, new(26, 400, 0, 3));
        That(first.Accepted && first.Plan!.After.Find(source.Position)!.Core.Extension114 == new ItemExtension(0x77, 0xA0000003) &&
            first.Plan.After.Find(new(InventoryListType.Main, 30))?.Core.Count == 4 &&
            first.Plan.After.Find(new(InventoryListType.Main, 31))?.Core.Count == 5,
            "首次诉求只扣首次材料，按槽写能力编号并保留扩展标志与其余字节");
        var firstCharacter = character with { Inventory = first.Plan!.After };
        var repeat = service.Apply(firstCharacter, new(26, 400, 0, 1));
        That(repeat.Accepted && repeat.Plan!.After.Find(new(InventoryListType.Main, 31))?.Core.Count == 4 &&
            repeat.Plan.After.Find(source.Position)!.Core.Extension114.Value == 0xA0000001,
            "重新祈求覆盖所选槽并额外扣重置材料");
        var second = service.Apply(firstCharacter, new(26, 400, 1, 11));
        That(second.Accepted && second.Plan!.After.Find(source.Position)!.Core.Extension114.Value == 0xA0000B03 &&
            second.Plan.After.Find(new(InventoryListType.Main, 31))?.Core.Count == 5,
            "第二权能首次诉求不按第一权能已存在误收重置材料");
        foreach (var rejected in new[]
        {
            service.Apply(firstCharacter, new(26, 400, 1, 4)),
            service.Apply(firstCharacter, new(26, 400, 2, 11)),
            service.Apply(firstCharacter, new(26, 400, 0, 99)),
            service.Apply(firstCharacter, new(26, 401, 0, 1)),
            service.Apply(character with { Inventory = character.Inventory with
                { Items = character.Inventory.Items.Where(item => item.Position.List != InventoryListType.Equipment).ToImmutableArray() } }, new(26, 400, 0, 3))
        })
            That(!rejected.Accepted && rejected.Plan is null, "重复状态条件、越界能力、源编号不符及未装备权能套装均拒绝");
        That(character.Inventory.Find(source.Position) == source && character.Inventory.Gold == 600000,
            "黑暗诉求规划不改输入快照、金币和其他打造");
    }
}
