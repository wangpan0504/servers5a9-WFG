using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonItemDropCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        ItemTemplate Template(int id, ItemKind kind, int grade, int rarity, int weight) =>
            new(id, kind, 0, 1, 0, 1000, 1, rarity, grade, "[free]", ushort.MaxValue, 0, "合成掉落") { CreationRate = weight };
        ItemTemplate[] source = [Template(100, ItemKind.Consumable, 1, 0, 2), Template(101, ItemKind.Consumable, 1, 0, 3),
            Template(102, ItemKind.Material, 1, 0, 7), Template(103, ItemKind.Equipment, 2, 1, 11)];
        var catalog = DungeonItemDropCatalog.Create(source);
        var pool = catalog.Pool(ItemKind.Consumable, 1, 1, 0);
        That(pool.TotalWeight == 5 && Enumerable.Range(0, 5).Select(roll => pool.Select(roll).Id)
            .SequenceEqual(new[] { 100, 100, 101, 101, 101 }), "物品候选按原正权重覆盖每个区间，不等权替换");
        That(catalog.Pool(ItemKind.Material, 1, 1, 0).Select(0).Id == 102 &&
            catalog.Pool(ItemKind.Equipment, 1, 2, 1).Select(10).Id == 103 &&
            catalog.Pool(ItemKind.Equipment, 1, 1, 1).Entries.IsEmpty,
            "类别、grade区间和稀有度完整过滤，不借另一类别或rarity0补空池");
        var referenceStack = catalog.ReferencePool(false, 1, 1, 0);
        That(referenceStack.TotalWeight == 3 && Enumerable.Range(0, 3).Select(roll => referenceStack.Select(roll).Id)
            .SequenceEqual(new[] { 100, 101, 102 }), "参考堆叠品候选兼容消耗品和材料，正creation rate只决定资格，选择等权");
        That(catalog.ReferencePool(true, 5, 7, 1).Select(10).Id == 103 &&
            catalog.ReferencePool(true, 1, 1, 6).Entries.IsEmpty,
            "参考装备可回退最近较低grade，真实空池不能借堆叠品补造装备");
        source[0] = source[0] with { CreationRate = 99 };
        var mixed = DungeonItemDropCatalog.Create([.. source, Template(104, ItemKind.Profession, 1, 0, 9),
            Template(105, ItemKind.Consumable, 2, 0, 1)]).ReferencePool(false, 1, 2, 6);
        That(mixed.Entries.Select(row => row.Template.Id).SequenceEqual(new[] { 100, 101, 102, 104, 105 }) && mixed.TotalWeight == 5,
            "跨等级及稀有度回退保留消耗品、材料、副职业材料的原候选顺序和等权区间");
        That(pool.TotalWeight == 5 && pool.Entries[0].Template.CreationRate == 2,
            "源集合替换不能改变已准备的权重和模板");
        Reject<ArgumentOutOfRangeException>(() => pool.Select(5), "抽样上界拒绝，不回退到最后候选");
        Reject<InvalidDataException>(() => DungeonItemDropCatalog.Create([source[1], source[1]]), "重复模板拒绝，不造成双倍概率");
        Reject<InvalidDataException>(() => DungeonItemDropCatalog.Create([source[1] with { CreationRate = 0 }]), "非生成候选不能借池获得资格");
        That(ItemCatalog.CreationRate(new PvfDocument("[creation rate]\n0")) == 0 &&
            ItemCatalog.CreationRate(new PvfDocument("[creation rate]\n17")) == 17 &&
            ItemCatalog.CreationRate(new PvfDocument("[name]\n`合成`")) == 0,
            "creation rate缺失、显式0与正权重保持区别");
        foreach (string value in new[] { "-1", "1 2", "abc" })
            Reject<InvalidDataException>(() => ItemCatalog.CreationRate(new PvfDocument("[creation rate]\n" + value)),
                "非法creation rate声明拒绝: " + value);
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var equipment = source[3] with { StackLimit = 1, Durability = 55, AttachType = "[sealing]" };
        int calls = 0;
        var dropped = DungeonDropCreation.Create(equipment, 1, bound => { calls++; That(bound == int.MaxValue,
            "装备种子抽样域不缩小为概率分母"); return 0; }, now);
        That(calls == 1 && dropped.Value == 0 && dropped.Durability == 55 && dropped.SealFlag == 1 && dropped.ExpireTime == 0,
            "合法零随机品级装备保留资源满耐久、封装和永久期限");
        var material = DungeonDropCreation.Create(source[2], 7, _ => throw new InvalidOperationException("材料不应随机装备属性"), now);
        That(material.Count == 7 && material.Kind == ItemKind.Material && material.ItemId == 102,
            "材料数量沿原创建器保存，不消耗装备随机种子");
        var profession = source[2] with { Id = 110, Kind = ItemKind.Profession };
        var professionalPool = new DungeonItemDropPool(new[] { profession });
        var professionalPlan = new DungeonDropGenerationPlan("合成副职业材料", [new(10000, professionalPool, 2, 2)]);
        That(professionalPlan.Generate(_ => 0, now).Single().Item is { Kind: ItemKind.Profession, Count: 2 },
            "资源声明的副职业材料复用已有堆叠Core和材料页，不因枚举不同从掉落中丢失");
        Reject<InvalidDataException>(() => DungeonDropCreation.Create(equipment, 1, _ => -1, now), "非法随机值不能改成满品级成功");
        Console.WriteLine($"PASS {Count - checks} 资源掉落候选权重与分类断言");
    }

    public static void RunResources(DungeonItemDropCatalog catalog)
    {
        int candidates = 0;
        foreach (var kind in new[] { ItemKind.Equipment, ItemKind.Consumable, ItemKind.Material })
        for (int rarity = 0; rarity <= 6; rarity++)
            candidates += catalog.Pool(kind, 1, 200, rarity).Entries.Length;
        That(candidates > 0, "匹配资源正creation rate候选已完整装配，目录存在不代表随机计算已接入");
        Console.WriteLine($"PASS 匹配资源掉落候选{candidates}项（完整模板解析，不代表客户端验收）");
    }
}
