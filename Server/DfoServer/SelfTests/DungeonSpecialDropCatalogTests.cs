using PvfLib;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonSpecialDropCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        var names = new Dictionary<int, string>();
        PvfToken Tag(string value) { int key = names.Count + 1; names.Add(key, value); return new(3, key); }
        string Resolve(int key) => names[key];
        var source = new List<PvfToken> { Tag("[independent drop]") };
        int[] row = [0, 42, 0, 500000, 500001, 0, 0, 0, 2, 3, 0, 0, 0, 1, 3, -1, 1];
        source.AddRange(row.Select(value => new PvfToken(0, value)));
        source.Add(Tag("[list]")); source.AddRange(new[] { 1000, 2, 1001, 1 }.Select(value => new PvfToken(0, value)));
        source.Add(Tag("[/list]")); source.Add(Tag("[/independent drop]"));
        var parsed = DungeonSpecialDropCatalog.ParseIndependent(source, Resolve).Single();
        That(parsed.MonsterId == 42 && parsed.Probabilities[1] == 500001 && parsed.Attempts[1] == 3 &&
            parsed.MinimumLevel == 1 && parsed.MaximumLevel == 3 && parsed.Difficulty == -1 &&
            parsed.Items.SequenceEqual(new[] { new DungeonSpecialDropItem(1000, 2), new DungeonSpecialDropItem(1001, 1) }),
            "独立掉落保留难度五列、次数及原物品权重，不把权重当概率");
        var reference = source.ToArray(); reference[17] = new(0, 2);
        var opaque = DungeonSpecialDropCatalog.ParseIndependent(reference, Resolve).Single();
        That(opaque.ListFlag == 2 && opaque.ListValues.SequenceEqual(new[] { 1000, 2, 1001, 1 }) && opaque.Items.IsEmpty,
            "尚未消费的列表引用保留原值，不误当内联物品表");
        Reject<InvalidDataException>(() => DungeonSpecialDropCatalog.ParseIndependent(source.Take(18).ToArray(), Resolve),
            "列表缺失不能作为正常零掉落");
        var world = new List<PvfToken> { Tag("[world drop]") };
        world.AddRange(new[] { 1, 7, 1000, 2, 1001, 0, -1 }.Select(value => new PvfToken(0, value)));
        world.Add(Tag("[/world drop]"));
        var worldRow = DungeonSpecialDropCatalog.ParseWorld(world, Resolve).Single();
        That(worldRow.Level == 1 && worldRow.ReservedValue == 7 && worldRow.Items[1].Weight == 0,
            "世界等级、第二列和零权重保留，不把展示或保留值补成概率");
        Reject<InvalidDataException>(() => DungeonSpecialDropCatalog.ParseWorld(world.Take(7).ToArray(), Resolve),
            "世界列表缺少终止整数不能装载成功");
        var catalog = DungeonSpecialDropCatalog.Create([parsed, opaque, parsed with { EnemyType = 1 }], [worldRow]);
        That(catalog.Compatibility.IndependentListReferences == 1 && catalog.Compatibility.OtherEnemyTypes == 1 &&
            catalog.Compatibility.WorldReservedValues == 1, "兼容保留的分支可统计，不能被所有规则成功消费的结论掩盖");
        Console.WriteLine($"PASS {Count - checks} 独立/世界掉落资源保真与兼容分支断言");
    }

    public static void RunResources(DungeonSpecialDropCatalog catalog)
    {
        That(catalog.MissingPaths.IsEmpty && catalog.IndependentByMonster.Count > 0 && catalog.WorldByLevel.Count > 0,
            "匹配资源的独立/世界声明已加载，未实现列表引用另有兼容统计");
        Console.WriteLine($"PASS 独立掉落怪物{catalog.IndependentByMonster.Count}、世界等级{catalog.WorldByLevel.Count}、保留列表引用{catalog.Compatibility.IndependentListReferences}");
    }
}
