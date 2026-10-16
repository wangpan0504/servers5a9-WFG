using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using PvfLib;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDropRuleCatalogTests
{
    public static void RunCore()
    {
        int checks = Count;
        var names = new Dictionary<int, string>();
        var source = new List<PvfToken>();
        var common = new List<PvfToken>();
        void Field(List<PvfToken> output, string name, params int[] values)
        {
            int key = names.Count + 1;
            names.Add(key, "[" + name + "]");
            output.Add(new(3, key));
            output.AddRange(values.Select(value => new PvfToken(0, value)));
        }
        Field(source, "drop prob count", 1);
        Field(source, "drop prob", 1, 200, 986, 60, 250, 17, 15);
        Field(source, "basis of rarity dicision", Enumerable.Range(0, 4)
            .SelectMany(_ => new[] { 200000, 400000, 600000, 800000, 900000, 990000, 1000000 }).ToArray());
        foreach (var (name, count) in new[] { ("party member drop bonusrate", 20),
            ("dungeon difficulty drop bonusrate", 25), ("monster type drop bonusrate", 20) })
        {
            Field(source, name, Enumerable.Repeat(1, count).ToArray());
            source[^1] = new(2, BitConverter.SingleToInt32Bits(1.4f));
        }
        Field(source, "condition rate", new int[8]);
        Field(source, "first boss/named mob hunting", 1, 2);
        Field(source, "gold quantity", 1, 2, 3, 4);
        Field(source, "gold volume", 2, 3);
        Field(source, "item drop rarity control", new int[10]);
        Field(source, "item drop ref table", 1, 0, 2, 2, 1, 3);
        Field(common, "gold drop ref table", 1, 17, 13, 2, 23, 13);
        string Resolve(int key) => names[key];
        DungeonDropRuleCatalog Parse(IEnumerable<PvfToken> tokens) => DungeonDropRuleCatalog.Parse(tokens.ToArray(), common, Resolve);
        var parsed = Parse(source);
        var plan = DungeonDropRatePlan.Prepare(parsed, 1, 0, 0, 1);
        That(plan.Thresholds.SequenceEqual(new[] { 986, 60, 250, 17, 15 }) &&
            plan.Hit(0, 985) && !plan.Hit(0, 986) && plan.Hit(4, 14) && !plan.Hit(4, 15),
            "全部概率列按相同分母检查精确边界，第五列没有被当作零掉落");
        That(plan.GoldAmount(0, 1m) == 15 && plan.GoldAmount(26, 1m) == 19 &&
            DungeonDropRatePlan.Rarity(parsed, 0, 199999) == 0 && DungeonDropRatePlan.Rarity(parsed, 0, 200000) == 1 &&
            DungeonDropRatePlan.Rarity(parsed, 3, 999999) == 6,
            "金币波动整数取整与七档稀有度上界逐项验证，不使用平均数替代随机结果");
        Reject<InvalidDataException>(() => DungeonDropRatePlan.Prepare(parsed, 1, 4, 0, 1), "怪物类型不夹取成Boss");
        Reject<InvalidDataException>(() => DungeonDropRatePlan.Prepare(parsed, 1, 0, 5, 1), "未知难度不补中性倍率");
        Reject<NotSupportedException>(() => DungeonDropRatePlan.Prepare(parsed, 3, 0, 0, 1), "引用缺失不借其他等级补造掉落");
        Reject<ArgumentOutOfRangeException>(() => plan.Hit(0, 10000), "概率抽样上界不允许取模回绕");
        That(parsed.ProbabilityRows.Single().Values.SequenceEqual(new[] { 986, 60, 250, 17, 15 }),
            "全局掉落五列完整保留，不能沿用参考端四列截取");
        That(parsed.PartyBonus.Length == 20 && parsed.DifficultyBonus.Length == 25 && parsed.MonsterTypeBonus.Length == 20 &&
            parsed.DifficultyBonus[^1].ReferenceDecimal == 1.4m && parsed.GoldReferences[0] == new DungeonDropGoldReference(1, 17, 13),
            "整数和真实float倍率按完整矩阵读取，金币等级三列保持来源");
        ItemTemplate Template(int id, ItemKind kind, int weight) =>
            new(id, kind, 0, 1, 37, kind == ItemKind.Equipment ? 1 : 100, 1, 0, 1, "[free]",
                ushort.MaxValue, 0, "合成参考策略候选") { CreationRate = weight };
        var candidates = DungeonItemDropCatalog.Create([Template(1000, ItemKind.Consumable, 2),
            Template(1001, ItemKind.Material, 7), Template(2000, ItemKind.Equipment, 5)]);
        var referencePolicy = new DungeonResourceDropPolicy(parsed, candidates,
            id => new(id, "monster/合成任意模板.mob", null, null), _ => throw new InvalidOperationException("合成无显式物品"));
        var bossPlan = referencePolicy.Prepare(42, 1, 3, 1);
        That(bossPlan.Trials.Select(trial => trial.Threshold).SequenceEqual(new[] { 1183, 72, 300, 20 }) &&
            bossPlan.ReferenceQuality && parsed.ProbabilityRows[0].Values[4] == 15,
            "参考默认四通道对任意Boss模板生效，第五列保留且不作为整个策略的阻塞条件");
        var draws = new Queue<(int Bound, int Value)>([(27, 26), (10000, 1182),
            (10000, 71), (1000000, 0), (2, 1), (10000, 299), (1000000, 0), (5, 4),
            (999999997, 999999996), (10000, 19), (1000000, 200000), (2, 0)]);
        var assets = bossPlan.Generate(bound =>
        {
            var draw = draws.Dequeue(); That(bound == draw.Bound, "参考策略的金币、稀有度、候选与装备种子各用自己的抽样域");
            return draw.Value;
        }, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(draws.Count == 0 && assets.Length == 4 && assets[0].GoldAmount == 22 &&
            assets[1].Item!.ItemId == 1001 && assets[2].Item!.Value == 999999997 && assets[3].Item!.ItemId == 1000,
            "独立参考预期核对金币先抽、两类堆叠品、装备权重、同类稀有度回退及参考种子上界");
        var firstDraws = new Queue<(int Bound, int Value)>([(27, 0), (10000, 1183), (10000, 9999), (10000, 9999), (10000, 9999)]);
        That(bossPlan.Generate(bound => { var draw = firstDraws.Dequeue(); That(bound == draw.Bound, "未命中金币仍先消费波动");
            return draw.Value; }, DateTimeOffset.UtcNow).IsEmpty && firstDraws.Count == 0,
            "四通道概率临界未命中不制造保底奖励");
        var specialRule = new DungeonIndependentDropRule(0, 42, 1001, [500000, 500000, 0, 0, 0], [2, 2, 0, 0, 0],
            1, 3, 1, 0, []);
        var special = DungeonSpecialDropCatalog.Create([specialRule], [new(1, 0, [new(1000, 2), new(1001, 1)])]);
        var specialPolicy = new DungeonResourceDropPolicy(parsed, candidates,
            id => new(id, "monster/合成任意模板.mob", null, null), id => Template(id, id == 1001 ? ItemKind.Material : ItemKind.Consumable, 1), special);
        var specialPlan = specialPolicy.Prepare(42, 1, 3, 1);
        var specialAssets = specialPlan.Generate(bound => bound switch
        { 27 => 0, 10000 => 9999, 1000000 => 499999, 100000 => 2, _ => 0 }, DateTimeOffset.UtcNow);
        That(specialAssets.Select(asset => asset.Item!.ItemId).SequenceEqual(new[] { 1001, 1001, 1000 }) &&
            specialPlan.Trials.Length == 6 && specialPlan.Trials[4].Attempts == 2,
            "独立掉落执行声明的两次百万分母抽样，世界通道另按十万分母与权重选择");
        That(specialPlan.Generate(bound => bound switch
        { 27 => 0, 10000 => 9999, 1000000 => 500000, 100000 => 3, _ => 0 }, DateTimeOffset.UtcNow).IsEmpty &&
            specialPolicy.Prepare(42, 1, 3, 1, 4).Trials.Length == 5 && specialPolicy.Prepare(42, 1, 3, 0).Trials.Length == 5,
            "特殊抽样临界不命中，独立条件按副本基准等级与难度过滤，不用怪物等级替代副本条件");
        var changed = source.ToArray(); changed[9] = new(0, 99);
        That(parsed.ProbabilityRows[0].Values[4] == 15, "原token变更不污染已加载概率表");
        changed = source.ToArray(); changed[1] = new(0, 2);
        Reject<InvalidDataException>(() => Parse(changed), "概率行数不匹配拒绝，不补默认行");
        changed = source.ToArray(); changed[9] = new(0, -1);
        Reject<InvalidDataException>(() => Parse(changed), "负概率拒绝，不夹取或忽略第五列");
        Reject<InvalidDataException>(() => Parse(source.Concat(source.Take(2))), "重复概率计数字段拒绝");
        changed = source.ToArray();
        int bonus = Array.FindIndex(changed, token => token.Type == 2);
        changed[bonus] = new(2, BitConverter.SingleToInt32Bits(float.NaN));
        Reject<InvalidDataException>(() => Parse(changed), "非有限倍率不能产生资源成功");
        changed = source.ToArray(); changed[bonus - 1] = new(0, 16777217);
        That(Parse(changed).PartyBonus[^2] == new DungeonDropNumber(16777217, false), "整数倍率不经float缩窄，保留声明类型和精度");
        foreach (int bits in new[] { 0x3F800001, 1, int.MinValue, 0x7F7FFFFF })
        {
            changed = source.ToArray(); changed[bonus] = new(2, bits);
            That(Parse(changed).PartyBonus[^1] == new DungeonDropNumber(bits, true),
                "浮点相邻值、次正规数、负零及最大有限值原位保留，不经decimal舍入或归零");
        }
        Reject<NotSupportedException>(() => { _ = Parse(changed).PartyBonus[^1].ReferenceDecimal; },
            "参考decimal范围不足只拒绝该参考解释，不能把合法原浮点声明判成资源非法");
        Console.WriteLine($"PASS {Count - checks} 全局掉落原始规则保真断言");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var rules = global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf);
        That(rules.ProbabilityRows.Length == 10 && rules.ProbabilityRows[0].MinimumLevel == 1 &&
            rules.ProbabilityRows[0].MaximumLevel == 15 && rules.ProbabilityRows[0].Values.SequenceEqual(new[] { 986, 60, 250, 0, 0 }) &&
            rules.RarityDecision.Length == 28 && rules.ItemReferences.Length == 200 && rules.GoldReferences.Length == 200 &&
            rules.GoldReferences[0] == new DungeonDropGoldReference(1, 17, 13),
            "匹配PVF全局概率、等级引用及金币表可完整读取；不是掉落计算或界面验收");
    }
}
