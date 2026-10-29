using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer;

internal static class EquipmentCatalogTests
{
    private static readonly string[] Strings =
    [
        "[minimum level]", "[durability]", "[weight]", "[price]", "[sell price]", "[required skill]",
        "[usable job]", "[equipment type]", "[mage]", "[creator mage]", "[weapon]",
        "fixture.equ", "../outside.equ", "/fixture.equ", "\\fixture.equ", "C:" + "\\fixture.equ", "dir\\Fixture.equ", ""
    ];

    public static void Run(PvfArchive pvf, EquipmentCatalog catalog)
    {
        int checks = Count;
        int[] beastIds = [100300907, 100312601, 100322493];
        var expected = ItemCatalogTests.ReadOriginalIndexDeclarations(pvf, "equipment", 1);
        That(expected.Keys.Count(id => !beastIds.Contains(id)) == 178841,
            "原始装备列表保留178841项旧声明，新增魔兽声明逐项按实际列表判断");
        That(catalog.TemplateCount == expected.Count && catalog.TemplateIds.Count == expected.Count &&
            catalog.TemplateIds.Distinct().Count() == expected.Count &&
            catalog.TemplateIds.SequenceEqual(expected.Keys.Order()),
            "完整装备目录ID集合与独立原始列表逐项一致，无缺失或额外模板");
        string[] beastPaths = ["amulet/100300907.equ", "wrist/100312601.equ", "ring/100322493.equ"];
        for (int beastIndex = 0; beastIndex < beastIds.Length; beastIndex++)
        {
            int id = beastIds[beastIndex];
            if (expected.TryGetValue(id, out string? path))
                That(path == "equipment/character/common/" + beastPaths[beastIndex] &&
                    catalog.Get(id).ScriptPath == path, "已声明魔兽首饰绑定真实装备脚本：" + id);
            else
            {
                KeyNotFoundException? missing = null;
                try { _ = catalog.Get(id); }
                catch (KeyNotFoundException error) { missing = error; }
                That(missing?.Message == "装备列表没有指定模板ID", "未声明魔兽首饰精确报告索引缺失：" + id);
            }
        }
        int[] ids = [10400, 12400, 16400, 18400, 101010912, 102040571, 104010559, 106020596,
            108000525, 109000493, 106040493, 101030782, 114000248, 116000011];
        int[] weights = [1900, 1500, 800, 900, 2800, 2300, 1500, 2200, 3100, 2100, 2500, 4000, 3200, 2400];
        int[] prices = [500, 400, 200, 200, 700, 700, 700, 700, 700, 700, 900, 800, 700, 700];
        for (int i = 0; i < ids.Length; i++)
        {
            EquipmentFile definition = catalog.Get(ids[i]);
            That(definition.DeclaredMinimumLevel == 1 && definition.DeclaredMaximumDurability > 0 &&
                definition.DeclaredWeight == weights[i] && definition.DeclaredPrice == prices[i], "初装真实等级耐久重量价格声明");
            That(definition.DeclaredEquipmentType!.Label == (i switch
            {
                0 => "[coat]",
                1 => "[pants]",
                2 => "[waist]",
                3 => "[shoes]",
                _ => "[weapon]"
            }), "初装保留领域类型文字不映射wire槽");
            That(definition.DeclaredSellPrice is null && definition.UnsupportedFields.Contains("sell price"), "初装无卖价不推断价格折算");
            if (i < 4)
                That(definition.DeclaredUsableJobs!.SequenceEqual(new[] { "[all]" }), "四防具原始全职业标签");
        }
        var broom = catalog.Get(106040493);
        That(broom.DeclaredRequiredSkill == 91 && broom.DeclaredUsableJobs!.SequenceEqual(new[] { "[mage]", "[at mage]", "[creator mage]" }) &&
            broom.DeclaredEquipmentType == new EquipmentTypeDeclaration("[weapon]", 19), "扫把技能声明及原始职业文字参数");
        That(catalog.Get(101010912).DeclaredRequiredSkill is null &&
            catalog.Get(101010912).UnsupportedFields.Contains("required skill"), "缺技能声明保留unknown不造无条件资格");
        That(catalog.Get(109600041).DeclaredEquipmentType is null &&
            catalog.Get(109600041).UnsupportedFields.Contains("equipment type"), "真实时装重复类型声明不猜覆盖");
        That(catalog.Get(64506).DeclaredWeight is null && catalog.Get(64506).UnsupportedFields.Contains("weight"), "真实重复重量声明未知");
        That(catalog.Get(409040007).DeclaredPrice is null && catalog.Get(413030002).DeclaredPrice is null, "两项及三项重复价格不采用0默认");
        That(catalog.Get(400990077).DeclaredUsableJobs is null && catalog.Get(106501565).DeclaredUsableJobs is null, "真实宠物与时装重复职业块未知");
        That(catalog.Get(105520410).DeclaredMinimumLevel is null, "真实重复等级声明未知");
        That(ReferenceEquals(catalog.Get(106040493), broom), "定义不可变缓存复用");
        foreach (int id in expected.Keys)
        {
            EquipmentFile definition = catalog.Get(id);
            if (definition.TemplateId != id || definition.ScriptPath != expected[id] ||
                pvf.FindFile(definition.ScriptPath)?.DataType != 1 ||
                definition.DeclaredSellPrice is not null || !definition.UnsupportedFields.Contains("sell price"))
                throw new InvalidOperationException("FAIL 装备全索引路径、脚本类型或卖价边界: " + id);
        }
        That(true, $"原始列表全部{expected.Count}项真实装备脚本可读、路径一致且全部缺卖价");
        Reject<KeyNotFoundException>(() => catalog.Get(0));
        Reject<KeyNotFoundException>(() => catalog.Get(-1));
        Reject<KeyNotFoundException>(() => catalog.Get(int.MaxValue));

        PvfToken[] valid =
        [
            new(3, 0), new(0, 1), new(3, 1), new(0, 20), new(3, 2), new(0, 2500), new(3, 3), new(0, 900),
            new(3, 4), new(0, 100), new(3, 5), new(0, 91), new(3, 6), new(6, 8), new(6, 9),
            new(3, 7), new(6, 10), new(0, 19)
        ];
        EquipmentFile fixture = Parse(valid);
        That(fixture.DeclaredMinimumLevel == 1 && fixture.DeclaredMaximumDurability == 20 && fixture.DeclaredWeight == 2500 &&
            fixture.DeclaredPrice == 900 && fixture.DeclaredSellPrice == 100 && fixture.DeclaredRequiredSkill == 91 &&
            fixture.DeclaredUsableJobs!.SequenceEqual(new[] { "[mage]", "[creator mage]" }) &&
            fixture.DeclaredEquipmentType == new EquipmentTypeDeclaration("[weapon]", 19) && fixture.UnsupportedFields.Count == 0,
            "独立token完整定义无默认插入");
        That(Parse([]).UnsupportedFields.Count == 8 && Parse([]).DeclaredEquipmentType is null, "八项缺声明全部unknown");
        That(Parse([new(3, 2), new(0, 0)]).DeclaredWeight == 0 && Parse([new(3, 3), new(0, int.MaxValue)]).DeclaredPrice == int.MaxValue,
            "标量零及int上界保持声明值");
        foreach (int tag in Enumerable.Range(0, 6))
        {
            foreach (PvfToken[] invalid in new PvfToken[][]
            {
                [new(3, tag)], [new(3, tag), new(0, -1)], [new(3, tag), new(6, 8)],
                [new(3, tag), new(2, 1)], [new(3, tag), new(0, 1), new(0, 2)],
                [new(3, tag), new(0, 1), new(3, tag), new(0, 1)]
            })
                That(Parse(invalid).UnsupportedFields.Contains(Strings[tag][1..^1]), "错误类型负值空长重复标量全部unknown");
        }
        That(Parse([new(3, 6), new(0, 1)]).DeclaredUsableJobs is null, "职业类型错误unknown");
        That(Parse([new(3, 6), new(6, 8), new(6, 8)]).DeclaredUsableJobs is null, "重复职业文字unknown");
        That(Parse([new(3, 6), new(6, 17)]).DeclaredUsableJobs is null, "空职业文字unknown");
        That(Parse([new(3, 7), new(6, 10)]).DeclaredEquipmentType is null, "短装备类型unknown");
        That(Parse([new(3, 7), new(6, 10), new(0, -1)]).DeclaredEquipmentType is null, "负附加参数unknown");
        That(Parse([new(3, 7), new(6, 10), new(0, 19), new(0, 1)]).DeclaredEquipmentType is null, "长装备类型unknown");

        PvfToken[] index = [new(0, 1), new(6, 11), new(0, 2), new(6, 16)];
        That(Index(index)[1] == "equipment/fixture.equ" && Index(index)[2] == "equipment/dir/fixture.equ", "索引大小写反斜杠归一");
        Reject<InvalidDataException>(() => Index([]));
        Reject<InvalidDataException>(() => Index(index[..^1]));
        Reject<InvalidDataException>(() => Index([new(0, 0), new(6, 11)]));
        Reject<InvalidDataException>(() => Index([new(0, -1), new(6, 11)]));
        Reject<InvalidDataException>(() => Index([new(6, 1), new(6, 11)]));
        Reject<InvalidDataException>(() => Index([new(0, 1), new(0, 11)]));
        Reject<InvalidDataException>(() => Index([new(0, 1), new(6, 11), new(0, 1), new(6, 16)]));
        foreach (int badPath in new[] { 12, 13, 14, 15, 17 })
            Reject<InvalidDataException>(() => Index([new(0, 1), new(6, badPath)]));
        Reject<InvalidDataException>(() => PvfItemIndex.Equipment(index, id => Strings[id], _ => false));

        Console.WriteLine($"PASS {Count - checks} 装备全索引、初装声明及Unsupported资源断言");

    }

    private static EquipmentFile Parse(PvfToken[] tokens) => EquipmentFile.Parse(1, "equipment/fixture.equ", tokens, id => Strings[id]);
    private static Dictionary<int, string> Index(PvfToken[] tokens) => PvfItemIndex.Equipment(tokens, id => Strings[id], _ => true);
}
