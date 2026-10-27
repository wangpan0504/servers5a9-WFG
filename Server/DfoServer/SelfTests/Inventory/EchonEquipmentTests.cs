using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EchonEquipmentTests
{
    internal static EchonEquipmentCatalog Rules()
    {
        string[] grades = ["D", "C", "B", "A"];
        var individual = new CustomAbilityFile("warriors dungeon", [new("1", [1, 2]), new("2", [11])],
            new[] { 1, 2, 11 }.Select(option => new CustomAbilityOption(option,
                grades.Select((grade, at) => new CustomAbilityMaterial(grade, [(200 + option, 5 + at)])).ToImmutableArray())).ToImmutableArray())
        {
            Upgrades = grades.Select((grade, at) => new CustomAbilityMaterial(grade, [(220, new[] { 11, 11, 18, 34 }[at])])).ToImmutableArray(),
            GradeWeights = [("A", 200), ("B", 800), ("C", 3000), ("D", 6000)],
            Disjoint = [new("1", "C", [(230, 1)]), new("1", "B", [(230, 4)]), new("1", "A", [(230, 14)])]
        };
        var set = new CustomAbilitySetFile([new(1, 3, 6, 4,
            grades.SelectMany(grade => Enumerable.Range(3, 4).Select(parts => (grade, parts, parts * 50))).ToImmutableArray(),
            [(221, 2.2f)], 0.9f)], [(1, 50), (2, 60), (3, 50), (4, 10000)],
            [new("1", [1, 2], 0), new("2", [3], 4), new("3", [4], 0)]);
        return new(individual, set);
    }

    public static void RunCore()
    {
        var rules = Rules();
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0, echon: rules);
        ItemTemplate Template(int id, byte slot) => new(id, ItemKind.Equipment, slot, 90, 32, 1, 100, 5, 90,
            "[trade]", ushort.MaxValue, 0, "Echon") { Echon = new(1, 2, rules) };
        var templates = new[] { Template(100, 14), Template(101, 15), Template(102, 16), Template(103, 17) };
        catalog.PrefillTemplates(templates.Select(template => KeyValuePair.Create(template.Id, template)));
        var pieces = new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]");
        var service = new InventoryEchonEquipmentService(catalog, rules, pieces);
        int[] rolls = [0, 0, 0]; int at = 0;
        var created = InventoryRewardGrantService.Create(templates[0], echonDraw: _ => rolls[at++]);
        That(EchonEquipmentState.Read(created) == new EchonEquipmentState(1, 1, 0, 0, 3),
            "初始两项独立抽样允许重复，品级按资源权重选A");
        var fixedGrade = InventoryRewardGrantService.Create(templates[0], echonGrade: 1, echonDraw: _ => 0);
        That(EchonEquipmentState.Read(fixedGrade).Grade == 1, "礼盒指定斗气品级且保留两项初始能力");
        const string box = "[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[equipment]\n100 1\n[/equipment]\n[/booster select category]\n";
        const string quotedTag = "[name]\n`[custom ability grade]`\n";
        That(BoosterCatalog.Parse(quotedTag + "[custom ability grade]\n3\n" + box).CustomAbilityGrade == 3 &&
            BoosterCatalog.Parse(quotedTag + box).CustomAbilityGrade is null,
            "品级只读取真实根级声明，不让名称中的标签文字遮蔽或伪造品级");
        Reject<InvalidDataException>(() => InventoryRewardGrantService.Create(templates[0], echonGrade: 4), "非法斗气品级不生成实例");
        var source = new InventoryItem(1, new(InventoryListType.Main, 9),
            new EchonEquipmentState(1, 2, 0, 0, 0).Apply(created with { Attr = 15, EnchantCardId = 3168, GenuineUpgrade = 8 }));
        var character = new Character(1, 0, "Echon", 0, new(1, 0, 10, 10), null)
        {
            Level = 95,
            Inventory = new([source,
                new(2, new(InventoryListType.Main, 10), new EchonEquipmentState(1, 11, 0, 0, 1).Apply(InventoryRewardGrantService.Create(templates[1]))),
                new(3, new(InventoryListType.Main, 11), new EchonEquipmentState(2, 11, 0, 0, 2).Apply(InventoryRewardGrantService.Create(templates[2]))),
                new(4, new(InventoryListType.Main, 12), new EchonEquipmentState(2, 11, 0, 0, 3).Apply(InventoryRewardGrantService.Create(templates[3]))),
                new(5, new(InventoryListType.Main, 121), new(220, ItemKind.Material, 100)),
                new(6, new(InventoryListType.Main, 122), new(211, ItemKind.Material, 100)),
                new(7, new(InventoryListType.Main, 123), new(221, ItemKind.Material, 1000))])
        };
        var upgrade = service.Upgrade(character, 9);
        var upgraded = upgrade.Plan!.After.Find(source.Position)!;
        That(upgrade.Accepted && upgraded.Uid == source.Uid && upgraded.Core == source.Core with { Extension114 = new(1, 0x201) } &&
            upgrade.Plan.After.Find(new(InventoryListType.Main, 121))!.Core.Count == 89,
            "D升C扣目标档材料11，完整保留UID和其余打造");
        var changed = service.ChangeOption(character, new(9, 0, 11));
        That(changed.Accepted && EchonEquipmentState.Read(changed.Plan!.After.Find(source.Position)!.Core) == new EchonEquipmentState(11, 2, 0, 0, 0) &&
            changed.Plan.After.Find(new(InventoryListType.Main, 122))!.Core.Count == 95,
            "变更按目标能力及当前档计费，只改指定位置");
        foreach (var request in new[] { new EchonOptionRequest(9, 0, 1), new(9, 2, 11), new(9, 0, 255) })
            That(service.ChangeOption(character, request) is { Accepted: false, Error: 1, Plan: null },
                "重复、越界和未知属性用专用错误1拒绝且无计划");
        That(service.ChangeOption(character, new(100, 0, 11)) is { Accepted: false, Error: 2, Plan: null },
            "不存在的道具用专用错误2拒绝");
        That(service.Upgrade(character, 12) is { Accepted: false, Error: 7, Plan: null }, "A档用最高等级错误7拒绝");
        var locked = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Replace(source,
            source with { Core = source.Core with { EquipmentLockId = 1 } }) } };
        That(service.Upgrade(locked, 9) is { Accepted: false, Error: 11, Plan: null }, "安全锁装备用不可使用错误11拒绝");
        var expired = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Replace(source,
            source with { Core = source.Core with { ExpireTime = 1 } }) } };
        That(service.Upgrade(expired, 9) is { Accepted: false, Error: 11, Plan: null }, "到期装备用不可使用错误11拒绝");
        var poor = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Where(item => item.Core.ItemId != 220).ToImmutableArray() } };
        That(service.Upgrade(poor, 9) is { Accepted: false, Error: 8, Plan: null }, "缺料用专用错误8拒绝，不提交装备变化");
        var noMaterials = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Where(item => item.Core.Kind == ItemKind.Equipment).ToImmutableArray() } };
        That(service.ChangeOption(noMaterials, new(9, 0, 11)) is { Accepted: false, Error: 8, Plan: null } &&
            service.AssignSet(noMaterials, new([1], [9, 10, 11])) is { Accepted: false, Error: 8, Plan: null },
            "词条和套装缺料同样使用专用错误8，无资产计划");
        var assigned = service.AssignSet(character, new([1], [9, 10, 11]));
        var set = assigned.Plan!.After.Find(source.Position)!.Core.EchonSet!;
        That(assigned.Accepted && set is { Index: 1, Parts: 3, Grade: 1, First: 1 } &&
            assigned.Plan.After.Items.Count(item => item.Core.EchonSet == set) == 3,
            "三件套品级取所有目标档位整型平均，定义随三件完整实例保存");
        That(assigned.Plan.After.Find(new(InventoryListType.Main, 123))!.Core.Count == 882,
            "150点预算分配50基础点消耗54点，float32乘2.2截断118材料");
        var reassigned = service.AssignSet(character with { Inventory = assigned.Plan.After }, new([1], [10, 11, 12]));
        That(reassigned.Accepted && reassigned.Plan!.After.Find(source.Position)!.Core.EchonSet is null &&
            EchonEquipmentState.Read(reassigned.Plan.After.Find(source.Position)!.Core).SetIndex == 0 &&
            reassigned.Plan.After.Find(new(InventoryListType.Main, 123))!.Core.Count == 870,
            "重新赋予扣除旧材料90%折抵，旧集合退役并清除关联字节");
        foreach (var request in new[] { new EchonSetRequest([1, 2], [9, 10, 11]), new([3], [9, 10, 11]), new([4], [9, 10, 11]) })
            That(service.AssignSet(character, request) is { Accepted: false, Error: 4, Plan: null },
                "同组、选项件数条件和点数不足用套装属性错误4整批拒绝");
        That(service.AssignSet(character, new([], [9, 10, 11])) is { Accepted: false, Error: 3, Plan: null } &&
            service.AssignSet(character, new([1], [9, 9, 10])) is { Accepted: false, Error: 9, Plan: null } &&
            service.AssignSet(character, new([1], [9, 10])) is { Accepted: false, Error: 6, Plan: null },
            "属性数量、重复部位和套装件数分别使用专用错误3、9、6");
        var duplicate = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Add(source with { Uid = 8, Position = new(InventoryListType.Main, 13) }) } };
        That(service.AssignSet(duplicate, new([1], [9, 10, 13])) is { Accepted: false, Error: 9, Plan: null },
            "不同槽同部位仍使用重复部位错误9");
        var fifthTemplate = Template(105, 18) with { Echon = new(1, 3, rules) };
        catalog.PrefillTemplates([KeyValuePair.Create(105, fifthTemplate)]);
        var mixedArmor = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Add(
            new(8, new(InventoryListType.Main, 13), new EchonEquipmentState(1, 11, 0, 0, 0).Apply(
                InventoryRewardGrantService.Create(fifthTemplate)))) } };
        That(service.AssignSet(mixedArmor, new([1], [9, 10, 11, 12, 13])) is { Accepted: false, Error: 10, Plan: null },
            "五件防具材质不一致用专用错误10拒绝");
        byte[] wire = EchonEquipmentPackets.Set(0x1234, new(1, 6, 1, 34, 100, 122, 3));
        That(wire.SequenceEqual(new byte[] { 0x34, 0x12, 0, 0, 1, 6, 1, 34, 100, 122, 0, 3 }),
            "1282固定12字节并保留第五物理选项零位");
        var core = assigned.Plan.After.Find(source.Position)!.Core;
        That(JsonSerializer.Deserialize<ItemCore>(JsonSerializer.Serialize(core)) == core, "类型化套装完整JSON往返");
        That(JsonSerializer.Deserialize<ItemCore>("{\"ItemId\":100,\"Kind\":1,\"Value\":1}")!.EchonSet is null,
            "存量核心无套装字段不补造能力或奖励");
        var disjoint = InventoryEchonEquipmentService.DisjointRewards(new EchonEquipmentState(1, 1, 0, 0, 3).Apply(created), templates[0]);
        That(disjoint is [{ ItemId: 230, Count: 28 }], "重复两项A级能力分解按每项追加14个材料");
        var modernRules = new EchonEquipmentCatalog(new CustomAbilityFile("new warriors dungeon",
            [new("1", [1, 2]), new("2", [11, 12]), new("3", [21])],
            new[] { 1, 2, 11, 12, 21 }.Select(option => new CustomAbilityOption(option,
                [new("D", [(221, 40)])])).ToImmutableArray())
        {
            GradeWeights = [("D", 10000)], OptionCategories = [(1, 2), (2, 2), (11, 0), (12, 0), (21, 1)],
            Disjoint = [new("1", "D", [(230, 4)]), new("2", "D", [(230, 4)]), new("3", "D", [(230, 4)])]
        }, new([], [], []));
        var modernTemplate = templates[0] with { Id = 104, Echon = new(0, 2, modernRules, 11, 21) };
        catalog.PrefillTemplates([KeyValuePair.Create(104, modernTemplate)]);
        var modernCore = InventoryRewardGrantService.Create(modernTemplate, echonDraw: _ => throw new Exception("Unexpected draw"));
        That(EchonEquipmentState.Read(modernCore) == new EchonEquipmentState(11, 21, 0, 0, 0),
            "新版斗气按模板固定两项与D档，不复用旧版随机规则");
        var modernCharacter = character with { Inventory = character.Inventory with
            { Items = character.Inventory.Items.Replace(source, source with { Core = modernCore }) } };
        var modernChange = service.ChangeOption(modernCharacter, new(9, 0, 1));
        That(modernChange.Accepted && EchonEquipmentState.Read(modernChange.Plan!.After.Find(source.Position)!.Core).First == 1 &&
            modernChange.Plan.After.Find(new(InventoryListType.Main, 123))!.Core.Count == 960,
            "新版斗气属性改造使用自身规则扣40材料");
        That(service.ChangeOption(modernCharacter, new(9, 1, 1)) is { Accepted: false, Error: 1, Plan: null } &&
            service.ChangeOption(modernCharacter, new(9, 0, 21)) is { Accepted: false, Error: 1, Plan: null } &&
            service.Upgrade(modernCharacter, 9) is { Accepted: false, Error: 11, Plan: null } &&
            service.AssignSet(modernCharacter, new([1], [9, 10, 11])) is { Accepted: false, Error: 5, Plan: null },
            "新版所有属性强化禁止改造，不进入旧版升档及自定义套装");
        That(InventoryEchonEquipmentService.DisjointRewards(modernCore, modernTemplate) is [{ ItemId: 230, Count: 8 }],
            "新版两项初始能力各追加4个分解材料");
        That(EchonEquipmentRequests.Option([9, 0, 1, 11]) == new EchonOptionRequest(9, 1, 11) &&
            EchonEquipmentRequests.Upgrade([9, 0]) == 9 && EchonEquipmentRequests.Set([1, 1, 3, 9, 0, 10, 0, 11, 0]).Slots.SequenceEqual(new short[] { 9, 10, 11 }),
            "本版三请求严格按槽、位置、选项和套装列表解析");
        foreach (byte[] body in new byte[][] { [], [1], [1, 1, 2, 9, 0, 10, 0], [1, 1, 3, 9, 0, 9, 0, 11, 0],
            [1, 0, 3, 9, 0, 10, 0, 11, 0], [1, 1, 3, 9, 0, 10, 0, 11, 0, 0] })
            Reject<InvalidDataException>(() => EchonEquipmentRequests.Set(body), "套装请求拒绝截断、尾部、重复和零选项");
        Console.WriteLine("Echon equipment core tests passed.");
    }
}
