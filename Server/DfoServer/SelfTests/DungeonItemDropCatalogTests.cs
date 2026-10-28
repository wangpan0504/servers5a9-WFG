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
        ParsedCandidateReuse();
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

    static void ParsedCandidateReuse()
    {
        var equipment = new Dictionary<int, string> { [100] = "child.equ", [101] = "avatar.equ", [102] = "bad.equ", [103] = "parallel.equ" };
        var items = new ItemCatalog(null!, equipment, new Dictionary<int, string>(), 1000);
        const string coat = "[equipment type]\n`[coat]`\n[creation rate]\n7\n[grade]\n4\n[rarity]\n1\n[durability]\n12";
        int reads = 0;
        var merged = EquipmentFile.ReadScript(path =>
        {
            reads++;
            return path switch { "equipment/child.equ" => "[import script]\n`base.equ`", "equipment/base.equ" => coat,
                _ => throw new FileNotFoundException(path) };
        }, "equipment/child.equ");
        var candidate = items.ParsedDropCandidate(100, "child.equ", merged)!;
        That(reads == 2 && candidate is { Id: 100, Kind: ItemKind.Equipment, Grade: 4, Rarity: 1, CreationRate: 7, Durability: 12 },
            "正候选复用完整import文档，不需要再次读取归档，保留模板字段");
        That(ReferenceEquals(candidate, items.Get(100)) && ReferenceEquals(candidate, items.ParsedDropCandidate(100, "child.equ", merged)),
            "已装配模板与普通Get保持同一缓存对象，预筛不替换既有模板");
        That(items.ParsedDropCandidate(102, "bad.equ", new("[creation rate]\n0\n[equipment type]\n`[unsupported]`")) is null &&
            items.ParsedDropCandidate(102, "bad.equ", new("[equipment type]\n`[unsupported]`")) is null,
            "零权重或未声明权重不进入模板解析，不扩大到全目录Get");
        Reject<InvalidDataException>(() => items.ParsedDropCandidate(100, "child.equ", new("[creation rate]\n-1")),
            "即使模板已缓存，预筛非法权重仍拒绝");
        Reject<InvalidDataException>(() => items.ParsedDropCandidate(102, "bad.equ", new("[creation rate]\n1\n[equipment type]\n`[unsupported]`")),
            "正权重完整模板解析失败不能静默移除候选");
        That(items.ParsedDropCandidate(102, "bad.equ", new(coat)) is { Id: 102 }, "解析失败不占用single-flight或污染模板缓存");
        var parallel = new ItemTemplate?[64];
        Parallel.For(0, parallel.Length, index => parallel[index] = items.ParsedDropCandidate(103, "parallel.equ", new(coat)));
        That(parallel.All(item => ReferenceEquals(item, parallel[0])) && ReferenceEquals(parallel[0], items.Get(103)),
            "并发正候选共用原single-flight和模板缓存，不返回分裂实例");
        var avatar = new PvfDocument(coat.Replace("[coat]", "[skin avatar]"));
        var special = items.ParsedDropCandidate(101, "avatar.equ", avatar)!;
        That(special is { Kind: ItemKind.Avatar, CreationRate: 7 } && !items.AcceptDropCandidate(special, false) && items.AcceptDropCandidate(special, true),
            "普通/祭坛特殊装备过滤不变，普通过滤不会破坏祭坛缓存模板");
        var missing = new FileNotFoundException("fixture");
        try
        {
            EquipmentFile.ReadScript(_ => throw missing, "equipment/missing.equ");
            That(false, "缺失脚本不得返回文档");
        }
        catch (InvalidDataException error)
        {
            That(ReferenceEquals(error.InnerException, missing), "预筛import保留缺失脚本包装及原始异常，不降为空候选");
        }
    }

    public static void RunParsedResources(PvfArchive pvf)
    {
        var equipment = PvfScript.List(pvf.ReadText("equipment/equipment.lst"));
        var stackable = PvfScript.List(pvf.ReadText("stackable/stackable.lst"));
        var foreign = new PvfDocument("[equipment type]\n`[coat]`\n[creation rate]\n7\n[grade]\n99\n[rarity]\n6");
        foreach (bool equipmentFirst in new[] { true, false })
        {
            var equips = new Dictionary<int, string>();
            var stacks = new Dictionary<int, string>();
            if (equipmentFirst)
            {
                equips[100] = equipment[10400];
                stacks[100] = "noncanonical.stk";
            }
            else
                stacks[100] = stackable[1013];
            var expected = new ItemCatalog(pvf, equips, stacks, 1000).Get(100);
            var items = new ItemCatalog(pvf, equips, stacks, 1000);
            var actual = items.ParsedDropCandidate(100, "noncanonical.stk", foreign)!;
            That(System.Text.Json.JsonSerializer.Serialize(actual) == System.Text.Json.JsonSerializer.Serialize(expected) &&
                ReferenceEquals(actual, items.Get(100)),
                "不同路径预筛文档不能进入规范ID缓存，装备优先与堆叠路径均沿用原Get完整字段");
            if (expected.CreationRate == 0)
                Reject<InvalidDataException>(() => DungeonItemDropCatalog.Create([actual]),
                    "非规范路径的正权重不能覆盖规范模板零权重资格");
        }
    }

    public static void RunSnapshotResources(PvfArchive pvf, ItemCatalog resources)
    {
        var paths = PvfScript.List(pvf.ReadText("equipment/equipment.lst"));
        var all = resources.DropCandidates(true).ToArray();
        string ordinary = paths[all.First(item => item.Kind == ItemKind.Equipment && item.Grade > 0).Id];
        string special = paths[all.First(item => item.Kind is ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment && item.Grade > 0).Id];
        string zero = paths.First(row => row.Key > 2 && ItemCatalog.CreationRate(EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + row.Value)) == 0).Value;
        var map = new Dictionary<int, string> { [102] = special, [100] = ordinary, [101] = zero };
        ItemCatalog New() => new(pvf, map, new Dictionary<int, string> { [900] = "missing-premium.stk" }, 1000, new HashSet<int> { 900 });
        var first = New();
        var normal = first.DropCandidates().ToArray();
        var altar = first.DropCandidates(true).ToArray();
        var reverse = New();
        var reverseAltar = reverse.DropCandidates(true).ToArray();
        var reverseNormal = reverse.DropCandidates().ToArray();
        string Fields(IEnumerable<ItemTemplate> items) => System.Text.Json.JsonSerializer.Serialize(items);
        That(normal.Select(item => item.Id).SequenceEqual(new[] { 100 }) && altar.Select(item => item.Id).SequenceEqual(new[] { 100, 102 }) &&
            Fields(normal) == Fields(reverseNormal) && Fields(altar) == Fields(reverseAltar),
            "普通/祭坛调用顺序不改变完整模板、原序、零权重与缺失premium排除");
        That(ReferenceEquals(normal[0], altar[0]) && first.DropCandidates() is not ItemTemplate[],
            "成功快照只共享模板引用，调用方不能改写底层数组");
        var concurrent = New();
        var batches = new ItemTemplate[16][];
        Parallel.For(0, batches.Length, index => batches[index] = concurrent.DropCandidates(index % 2 == 0).ToArray());
        That(batches.All(batch => ReferenceEquals(batch[0], batches[0][0])), "并发普通/祭坛装配共用成功快照和原模板单航班");
        var replacement = normal[0] with { Grade = normal[0].Grade + 1 };
        first.PrefillTemplates(new Dictionary<int, ItemTemplate> { [100] = replacement });
        That(ReferenceEquals(first.DropCandidates().Single(), replacement), "预置覆盖同步失效候选快照");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        IEnumerable<KeyValuePair<int, ItemTemplate>> Paused()
        {
            entered.Set();
            That(release.Wait(TimeSpan.FromSeconds(5)), "合成预置屏障有界释放");
            yield return new(100, replacement);
        }
        var filling = Task.Run(() => first.PrefillTemplates(Paused()));
        That(entered.Wait(TimeSpan.FromSeconds(5)), "合成预置已经持有快照门闩");
        var reading = Task.Run(() => first.DropCandidates().Single());
        release.Set();
        Task.WaitAll(filling, reading);
        That(ReferenceEquals(reading.Result, replacement), "并发预置与候选读取不能返回失效快照");
        var partial = replacement with { Grade = replacement.Grade + 1 };
        IEnumerable<KeyValuePair<int, ItemTemplate>> Broken()
        {
            yield return new(100, partial);
            throw new InvalidDataException("合成枚举失败");
        }
        Reject<InvalidDataException>(() => first.PrefillTemplates(Broken()));
        That(ReferenceEquals(first.DropCandidates().Single(), partial), "预置枚举失败后的部分覆盖不能沿用旧快照");
        first.PrefillTemplates(new Dictionary<int, ItemTemplate> { [100] = partial with { CreationRate = 0 } });
        That(first.DropCandidates().Single().CreationRate == 0, "快照仍按脚本预筛资格，不另按缓存模板权重静默删项");
        Reject<InvalidDataException>(() => DungeonItemDropCatalog.Load(first), "原目录仍严格拒绝预置零权重模板");
        var brokenPaths = new Dictionary<int, string> { [100] = ordinary, [101] = "missing-candidate.equ" };
        var retry = new ItemCatalog(pvf, brokenPaths, new Dictionary<int, string>(), 1000);
        Reject<InvalidDataException>(() => retry.DropCandidates().ToArray(), "任何候选解析失败不发布部分成功快照");
        brokenPaths[101] = ordinary;
        That(retry.DropCandidates().Select(item => item.Id).SequenceEqual(new[] { 100, 101 }), "失败不缓存异常，修复自测输入后仍能完整重试");
        var duplicated = new ItemCatalog(pvf, new Dictionary<int, string> { [100] = ordinary },
            new Dictionary<int, string> { [100] = ordinary }, 1000);
        That(duplicated.DropCandidates().Count() == 2, "跨名册重复ID仍保留重复和顺序，不提前去重");
        Reject<InvalidDataException>(() => DungeonItemDropCatalog.Load(duplicated), "下游重复模板拒绝合同不改变");
        Console.WriteLine("PASS 正候选成功快照、并发、过滤、预置失效及失败重试资源断言");
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
