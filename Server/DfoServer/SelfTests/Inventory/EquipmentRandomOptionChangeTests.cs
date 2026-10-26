using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EquipmentRandomOptionChangeTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(RandomOptionRequests.Change([25, 0, 0, 0, 2]) == new EquipmentRandomOptionChangeRequest(25, 0, 2) &&
            RandomOptionRequests.Change([255, 255, 1, 0, 255]) == new EquipmentRandomOptionChangeRequest(65535, 1, 255),
            "437固定5B，完整保留u16目标/保留字段与零基索引，语义交给业务验证");
        foreach (int size in new[] { 0, 4, 6, 8 })
            Reject<InvalidDataException>(() => RandomOptionRequests.Change(new byte[size]), "437不借用401包体");
        That(CargoPackets.Ack(new(true, 0, null)).SequenceEqual(new byte[] { 1 }), "437成功只有状态字节");
        foreach (ushort error in new ushort[] { 4, 10, 19, 0xD5 })
            That(CargoPackets.Ack(InventoryResult.Reject(error)).SequenceEqual(new byte[] { 0, (byte)error, 0 }),
                "437失败保留公共u16错误信封，不沿用401的单字节状态");
        var core = new ItemCore(100, ItemKind.Equipment, 1234567)
        {
            Durability = 33, Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 42,
            SealFlag = 1, SortLockFlag = 1, Rune = 8, GenuineUpgrade = 5, Extension97 = 0x12345678,
            GuardianGemKeys = 0x123456789ABCDEF0, Random0 = new(70, 10, 11), Random1 = new(71, 12, 13),
            Random2 = new(72, 14, 15), RandomState = 7, RandomChangedIndex = 255
        };
        var type = new EquipmentRandomOptionChangeType(1, [new(1, [(70, 1), (71, 1), (72, 1)])], 30, 55);
        var definition = new EquipmentRandomOptionChangeDefinition(100, [type], [new(0.8f, 1, 1)]);
        for (byte index = 0; index < 3; index++)
        {
            int scalar = 0;
            var changed = InventoryRandomOptionService.GenerateChange(core, index, definition,
                maximum => maximum == 26 ? scalar++ % 2 == 0 ? 0 : 25 : maximum - 1)!;
            var original = new[] { core.Random0, core.Random1, core.Random2 };
            That(changed.RandomChange == new RandomOption(original[index].Type, 24, 49) && changed.RandomChangedIndex == index &&
                changed.RandomChangeState == index &&
                (changed with { RandomChangedIndex = 255, RandomChange = default, RandomChangeState = 0 }) == core,
                "允许抽回原类型但排除另两条；两个scalar独立，原三词条、seed及完整装备属性保留");
            var slot = new InventorySlot(InventoryListType.Main, 25);
            var delta = InventoryPackets.Delta(new([new(77, slot, changed)]), InventoryListType.Main, [slot]);
            That(delta.Length == 126 && delta[3 + 71] == 3 && delta[3 + 82] == index && delta[3 + 83] == index &&
                delta[3 + 84] == original[index].Type && delta[3 + 85] == 24 && delta[3 + 86] == 49,
                "123B行82/83/84到86为原词条索引、候选状态和候选三元组，不覆盖原列或拼进ACK");
            That(InventoryRandomOptionService.GenerateChange(changed, index, definition, _ => 0) is { } next &&
                next.Random0 == core.Random0 && next.Random1 == core.Random1 && next.Random2 == core.Random2,
                "已有候选可以继续变换同一条，原始词条不丢失");
            That(InventoryRandomOptionService.GenerateChange(changed, (byte)((index + 1) % 3), definition,
                _ => throw new InvalidOperationException("拒绝不应抽取")) is null, "候选已锁定时另一条在随机前拒绝");
        }
        var empty = definition with { Types = [type with { Groups = [new(1, [(70, 1)])] }] };
        int draws = 0;
        That(InventoryRandomOptionService.GenerateChange(core, 1, empty, _ => { draws++; return 0; }) is null && draws == 2,
            "选定组排重后没有候选即拒绝，不无界重抽或暗中换组");
        That(InventoryRandomOptionService.GenerateChange(core, 3, definition, _ => throw new InvalidOperationException()) is null,
            "无效索引不进入随机");
        Reject<InvalidDataException>(() => InventoryRandomOptionService.GenerateChange(core, 0, definition, maximum => maximum),
            "越界随机源不能形成候选");
        Console.WriteLine($"PASS {Count - checks} 装备随机属性变换正文、候选排重、完整核心与独立字段断言");
    }

    internal static void RunCatalog(Func<Func<string, PvfToken[]>, Func<int, string>, RandomOptionCatalog> load, ItemTemplate template)
    {
        string[] strings = ["[modified option type]", "[/modified option type]", "[modified option selection]", "[/modified option selection]",
            "lt shoes", "[option modification]", "[/option modification]", "[different weight]", "[/different weight]",
            "[option value ratio]", "[/option value ratio]"];
        PvfToken I(int value) => new(0, value);
        PvfToken T(int at) => new(3, at);
        var files = new Dictionary<string, PvfToken[]>
        {
            ["randomizedoptionoverall1.etc"] = [T(0), I(2), I(-1), I(19), I(1), I(0), I(0),
                I(2), I(1), I(19), I(0), I(1), I(0), T(1)],
            ["optiongroupselection.etc"] = [T(2), I(2), I(-1), new(6, 4), I(1), I(1), I(1001), I(1000),
                I(2), I(1), new(6, 4), I(2), I(1), I(1000), I(1000), T(3)],
            ["randomizedoptionoverall2.etc"] = [T(5), I(1), I(50), I(500), I(20), I(300), I(3000), T(6),
                T(7), I(2), I(1), I(30), I(55), I(2), I(2), I(30), I(55), T(8)],
            ["optionnumbering.etc"] = [T(9), new(2, BitConverter.SingleToInt32Bits(0.8f)), I(1), I(1000), T(10)]
        };
        RandomOptionCatalog Load() => load(path => files[Path.GetFileName(path)], at => strings[at]);
        var catalog = Load();
        var rule = catalog.ResolveChange(template)!;
        That(rule.GoldCost == 300 && rule.Types[0].Weight == 1 && rule.Types[1].Weight == 0 &&
            rule.Types[0].Groups.Single().Options.Single().Type == 1 && catalog.Resolve(template)!.GoldCost == 100,
            "变换独立选表、精确需求等级、通用itemType优先；费用取首个上界且不改变原解封");
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        items.PrefillTemplates([new(template.Id, template)]);
        var source = new InventoryItem(1, new(InventoryListType.Main, 9), new(template.Id, ItemKind.Equipment, 123)
            { Random0 = new(1, 10, 10), RandomChangedIndex = 255 });
        var role = new DfoServer.Game.Characters.Character(1, 0, "Option", 0, new(1, 0, 10, 10), null)
            { Level = 95, Inventory = new([source], Gold: 1000) };
        var service = new InventoryRandomOptionService(items, catalog, _ => 0);
        InventoryQualificationTests.CheckInputs(role, value => service.Change(value, new(9, 0, 0)),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), source.Position);
        That(catalog.ResolveChange(template with { MinimumLevel = 18 }) is null &&
            catalog.ResolveChange(template with { Kind = ItemKind.Material }) is null &&
            catalog.ResolveChange(template with { Rarity = 0 }) is null,
            "缺精确等级、错误种类及稀有度不套用邻级或角色等级");
        files["randomizedoptionoverall1.etc"] = [T(0), I(2), I(1), I(19), I(0), I(1), I(0), T(1)];
        That(Load().ResolveChange(template)!.Types[1].Groups.Single().Options[0].Type == 1,
            "不存在通用行才使用具体itemType行");
        files["randomizedoptionoverall1.etc"] = [];
        That(Load().ResolveChange(template) is null && Load().Resolve(template) is not null,
            "缺变换配置只禁用变换，不破坏旧解封或伪造规则");
        files["randomizedoptionoverall1.etc"] = [T(0), I(2), I(-1), I(19), I(1), I(0), I(0), T(1)];
        files["randomizedoptionoverall2.etc"] = [T(5), I(20), I(300), I(3000), I(1), I(50), I(500), T(6),
            T(7), I(2), I(1), I(30), I(55), T(8)];
        Reject<InvalidDataException>(() => Load(), "逆序费用档位不能静默重排后冒充客户端首个上界规则");
    }
}
