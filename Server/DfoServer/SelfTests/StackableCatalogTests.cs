using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer;

internal static class StackableCatalogTests
{
    private static readonly string[] Strings =
    [
        "[stackable type]", "[stack limit]", "[price]", "[sell price]", "[minimum level]", "[item group name]",
        "[material]", "material group", "a.stk", "../outside.stk", "/a.stk", "C:" + "\\a.stk", "a.equ", "dir\\A.stk"
    ];

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = StackableCatalog.Load(pvf);
        That(catalog.TemplateCount == 81365 && catalog.TemplateIds.Count == 81365 && catalog.TemplateIds[0] == 0,
            "匹配索引全81365项包含金币ID0");
        StackableFile gold = catalog.Get(0);
        That(gold.ScriptPath == "stackable/gold.stk" && gold.TypeDeclaration!.Label == "[waste]" &&
            gold.TypeDeclaration.Parameters.SequenceEqual(new[] { 0 }) && gold.DeclaredMinimumLevel == 1 &&
            gold.DeclaredStackLimit is null && gold.DeclaredPrice is null, "金币资源缺容量价格而不赠造实例");
        StackableFile potion = catalog.Get(1051);
        That(potion.ScriptPath == "stackable/ptn_stone.stk" && potion.TypeDeclaration!.Label == "[waste]" &&
            potion.TypeDeclaration.Parameters.SequenceEqual(new[] { 6 }) && potion.DeclaredStackLimit == 1000 &&
            potion.DeclaredPrice == 300 && potion.DeclaredMinimumLevel == 1 && potion.DeclaredItemGroupName == "potion" &&
            potion.DeclaredSellPrice is null, "石化恢复剂实际原始声明");
        StackableFile cube = catalog.Get(3037);
        That(cube.ScriptPath == "stackable/material/cubepiece_clear.stk" && cube.TypeDeclaration!.Label == "[material]" &&
            cube.TypeDeclaration.Parameters.SequenceEqual(new[] { 2 }) && cube.DeclaredPrice == 100 &&
            cube.DeclaredStackLimit is null && cube.DeclaredItemGroupName == "cube stuff", "材料缺limit不猜容量");
        StackableFile recipe = catalog.Get(2631000);
        That(recipe.ScriptPath == "stackable/recipe/nrcp_lswd_2choro14.equ" && recipe.TypeDeclaration!.Label == "[recipe]" &&
            recipe.TypeDeclaration.Parameters.SequenceEqual(new[] { 1 }) && recipe.DeclaredPrice == 1000 &&
            recipe.DeclaredMinimumLevel is null, "真实列表中的equ扩展配方按token脚本读取，不猜装备分类");
        That(catalog.Get(10002064).TypeDeclaration!.Parameters.SequenceEqual(new[] { 3, 0 }), "实际优惠券保留额外参数而不猜枚举");
        That(catalog.Get(2660474).TypeDeclaration is null && catalog.Get(2660474).UnsupportedFields.Contains("stackable type") &&
            catalog.Get(10002199).DeclaredPrice is null && catalog.Get(10002199).UnsupportedFields.Contains("price"),
            "真实重复标签保留unsupported不猜覆盖规则");
        That(ReferenceEquals(catalog.Get(1051), potion), "lazy查询复用不可变定义");
        // 验证全索引均可读取，特殊字段可以未知，但不能漏掉模板或默默用另一个文件。
        foreach (int id in catalog.TemplateIds)
        {
            var definition = catalog.Get(id);
            if (definition.TemplateId != id || pvf.FindFile(definition.ScriptPath)?.DataType != 1)
                throw new InvalidOperationException("FAIL 堆叠索引模板路径或脚本类型不一致: " + id);
        }
        That(true, "全部81365项真实脚本可读且ID路径保持对应");
        Reject<KeyNotFoundException>(() => catalog.Get(-1));
        Reject<KeyNotFoundException>(() => catalog.Get(int.MaxValue));

        PvfToken[] valid =
        [
            new(3, 0), new(6, 6), new(0, 2), new(3, 1), new(0, 1000), new(3, 2), new(0, 100),
            new(3, 3), new(0, 10), new(3, 4), new(0, 1), new(3, 5), new(6, 7)
        ];
        var fixture = Parse(valid);
        That(fixture.TypeDeclaration!.Label == "[material]" && fixture.DeclaredStackLimit == 1000 && fixture.DeclaredPrice == 100 &&
            fixture.DeclaredSellPrice == 10 && fixture.DeclaredMinimumLevel == 1 && fixture.DeclaredItemGroupName == "material group" &&
            fixture.UnsupportedFields.Count == 0, "固定token单字段声明完整读取");
        That(Parse([]).DeclaredStackLimit is null && Parse([]).UnsupportedFields.Count == 6, "缺失字段全部未知");
        That(Parse([new(3, 0), new(6, 6), new(0, 1), new(0, 2), new(0, 3), new(0, 4)])
            .TypeDeclaration!.Parameters.SequenceEqual(new[] { 1, 2, 3, 4 }), "保留四个原始类型参数");
        That(Parse([new(3, 0), new(6, 6), new(0, 1), new(0, 2), new(0, 3)]).TypeDeclaration is null,
            "未确认的三参数形状unknown");
        That(Parse([new(3, 0), new(0, 2), new(0, 1)]).TypeDeclaration is null, "类型标签token不符不猜分类");
        That(Parse([new(3, 1), new(0, 5), new(0, 6)]).DeclaredStackLimit is null, "多项limit声明不猜首项");
        That(Parse([.. valid, new(3, 1), new(0, 99)]).DeclaredStackLimit is null, "重复标量不猜覆盖");
        That(Parse([new(3, 1), new(0, 0)]).DeclaredStackLimit == 0, "原始零声明保留而不替换为容量默认");
        Reject<InvalidDataException>(() => Parse([new(3, 1), new(0, -1)]));
        Reject<InvalidDataException>(() => Parse([new(3, 2), new(2, 100)]));
        PvfToken[] index = [new(0, 0), new(6, 8), new(0, 1), new(6, 13)];
        var paths = Index(index);
        That(paths[0] == "stackable/a.stk" && paths[1] == "stackable/dir/a.stk", "列表ID0及大小写反斜杠归一");
        Reject<InvalidDataException>(() => Index([]));
        Reject<InvalidDataException>(() => Index(index[..^1]));
        Reject<InvalidDataException>(() => Index([new(0, -1), new(6, 8)]));
        Reject<InvalidDataException>(() => Index([new(6, 0), new(6, 8)]));
        Reject<InvalidDataException>(() => Index([new(0, 1), new(0, 8)]));
        Reject<InvalidDataException>(() => Index([new(0, 1), new(6, 8), new(0, 1), new(6, 13)]));
        foreach (int badPath in new[] { 9, 10, 11 })
            Reject<InvalidDataException>(() => Index([new(0, 1), new(6, badPath)]));
        That(Index([new(0, 1), new(6, 12)])[1] == "stackable/a.equ", "索引中的非stk扩展允许作为真实token资源");
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable([new(0, 1), new(6, 12)], id => Strings[id],
            path => path != "stackable/a.equ"));
        Reject<InvalidDataException>(() => PvfItemIndex.Stackable(index, id => Strings[id], _ => false));
        Console.WriteLine($"PASS {Count - checks} 堆叠物品全索引、真实声明和非法token断言");

    }

    private static StackableFile Parse(PvfToken[] tokens) => StackableFile.Parse(1, "stackable/fixture.stk", tokens, id => Strings[id]);
    private static Dictionary<int, string> Index(PvfToken[] tokens) => PvfItemIndex.Stackable(tokens, id => Strings[id], _ => true);
}
