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
        TayberrsCardRewardTests.RunCore();
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
        var cardPrices = DungeonCardPriceCatalog.Parse([new(3, 1), new(0, 1), new(0, 100)], _ => "[gold card cost table]");
        int cardDraws = 0;
        // 空候选池真实走完付费牌规划；物品、钱包与碎片目录仅由未调用的发放阶段使用。
        var cards = new DungeonCardRewardPolicy(parsed, DungeonItemDropCatalog.Create([]), null!, null!, null!,
            random: _ => { cardDraws++; return 0; }, prices: cardPrices);
        var cardDungeon = new DungeonDefinition(1, "dungeon/合成活动隔离.dgn", 1, 1, [], []);
        var cardNow = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var goldEvent = new DfoServer.Game.Events.ServerEventState(new(64, 7, OverrideActive: true), true);
        var goldOptions = cards.PrepareOptions(cardDungeon, 0, 1, new(), cardNow, goldEvent);
        That(goldOptions is { DoublePaidItems: true, GoldCardEventRevision: 7, Price: 100, UseGoldCard: false, Paid.Gold: 0 } && cardDraws == 2,
            "黄金牌64沿真实无PVF空池规划保持双倍标记、版本及独立费用");
        foreach (bool active in new[] { true, false })
        {
            int previousDraws = cardDraws;
            var foreign = new DfoServer.Game.Events.ServerEventState(new(2532, 11, OverrideActive: active), active);
            Reject<InvalidDataException>(() => cards.PrepareOptions(cardDungeon, 0, 1, new(), cardNow, foreign),
                "2532无论开关均不能作为黄金牌64状态或revision输入");
            That(cardDraws == previousDraws, "非64活动在抽奖及翻牌规划之前拒绝");
        }
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
        var map = new DungeonMap("boss", new(0, 0), 1, "map/drop-preparation.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 1, 0, 0, 1, maze, map, map) { BasisLevel = 1, HellPartyMode = 1 };
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 2 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 200000 400000 600000 800000 900000 990000 1000000
            [item drop ref table] 1 0 2 2 1 3
            """), candidates);
        var prepare = specialPolicy.PrepareEntry(entry, hell);
        var monster = new DungeonPlannedMonster(0, 10, 42, 1, null) { SpawnType = 3 };
        var cached = prepare(monster);
        That(ReferenceEquals(cached, prepare(monster with { ObjectKey = 11, SourceOrdinal = 1 })),
            "同次入图相同掉落输入复用不可变规则，对象编号与位置不产生第二份候选池");
        foreach (var different in new[] { monster with { MonsterId = 43 }, monster with { CandidateLevel = 2 },
            monster with { SpawnType = 1 }, monster with { IsDummyMapBoss = true } })
            That(!ReferenceEquals(cached, prepare(different)), "怪物模板、等级、生成类型与奖励品级分别参与规则复用");
        That(!ReferenceEquals(cached, specialPolicy.PrepareEntry(entry, hell)(monster)) &&
            specialPolicy.PrepareEntry(entry with { BasisLevel = 4 }, hell)(monster).Trials.Length == 5 &&
            specialPolicy.PrepareEntry(entry with { Difficulty = 0 }, hell)(monster).Trials[0].Threshold == 986,
            "下次入图重新准备，副本等级与难度不会沿用上次计划");
        var rewarded = prepare(monster with { HellRewardRolls = 2 });
        var apc = prepare(monster with { SpawnType = 5, HellRewardRolls = 2 });
        That(rewarded.Trials.Length == cached.Trials.Length + 1 && rewarded.Trials[^1].Attempts == 2 &&
            apc.ResourceSource == "hell-party-apc" && apc.Trials.Length == 1 &&
            prepare(monster with { SpawnType = 5 }).Trials.IsEmpty && ReferenceEquals(cached, prepare(monster)),
            "深渊额外骰与普通计划分离，APC不套普通概率，去掉奖励骰仍恢复原普通计划");
        var pieceList = new EpicPieceCatalog("[piece list]\n8000 8001 9000 9001\n[/piece list]\n[equipment recipe info]\n[/equipment recipe info]");
        var pieceItems = new Dictionary<int, ItemTemplate>
        {
            [8000] = Template(8000, ItemKind.Equipment, 1) with { MinimumLevel = 80, Rarity = 4 },
            [9000] = Template(9000, ItemKind.Equipment, 1) with { MinimumLevel = 90, Rarity = 4 },
            [8001] = Template(8001, ItemKind.Material, 30), [9001] = Template(9001, ItemKind.Material, 70)
        };
        var fragmentCatalog = new EpicPieceDropCatalog(pieceList, id => pieceItems[id]);
        That(fragmentCatalog.Pool(84, 92).Entries.Select(row => row.Template.Id).SequenceEqual(new[] { 9001 }) &&
            fragmentCatalog.Pool(84, 92).TotalWeight == 70 && fragmentCatalog.Pool(1, 2).Entries.IsEmpty,
            "STK同为1级时按映射装备等级选碎片，不把材料等级或creation rate当掉率");
        var fragmentHell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 90 90 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 200000 400000 600000 800000 900000 990000 1000000
            [item drop ref table] 90 6 3
            """), candidates, fragmentCatalog);
        var emptyPlan = new DungeonDropGenerationPlan("碎片合成夹具", []);
        var fragmentPlan = fragmentHell.Extend(emptyPlan, 90, 1, 8, 0);
        That(fragmentPlan.Trials.Length == 2 && fragmentPlan.Trials[^1].Threshold == 1000 &&
            fragmentPlan.Trials[^1].Attempts == 6 && fragmentPlan.Trials[^1].Items!.Entries.Single().Template.Id == 9001 &&
            fragmentHell.Extend(emptyPlan, 90, 1, 8, 4, suppressEpicFragments: true).Trials.Length == 1 &&
            fragmentHell.Extend(emptyPlan, 90, 1, 0).Trials.IsEmpty,
            "默认开启本服碎片骰，只在深渊末波奖励点生成；哈林抑制和零奖励不混入碎片");
        int firstSamples = 0, secondSamples = 0;
        var firstAssets = cached.Generate(_ => { firstSamples++; return 0; }, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        var secondAssets = cached.Generate(bound => { secondSamples++; return bound - 1; }, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(firstAssets.Length > 0 && secondAssets.IsEmpty && firstSamples > 0 && secondSamples > 0 && cached.Trials.Length == 6,
            "共享计划每次死亡仍独立抽样，不缓存实际奖励或被深渊追加修改");
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
        VerifyLegendEquipment(parsed, entry);
        HarlemHellEpicDropTests.RunCore(parsed, entry);
        Console.WriteLine($"PASS {Count - checks} 全局掉落原始规则保真断言");
    }

    static void VerifyLegendEquipment(DungeonDropRuleCatalog rules, DungeonEntryPlan entry)
    {
        ItemTemplate Gear(int id, byte level, int weight) => new(id, ItemKind.Equipment, 0, level, 37, 1, 1, 4, 1,
            "[free]", ushort.MaxValue, 0, "合成星空史诗") { CreationRate = weight };
        var old = Gear(9000, 90, 2);
        var first = Gear(9500, 95, 3);
        var second = Gear(9501, 95, 7);
        var future = Gear(10000, 100, 11);
        var ordinary = old with { Id = 9100, Rarity = 0, CreationRate = 5 };
        var material = new ItemTemplate(3000, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成旧史诗碎片") { CreationRate = 1 };
        var templates = new[] { old, first, second, future, ordinary, material }.ToDictionary(item => item.Id);
        var candidates = DungeonItemDropCatalog.Create(templates.Values);
        var special = DungeonSpecialDropCatalog.Create(
            [new(0, 42, 0, [500000, 500000, 0, 0, 0], [2, 2, 0, 0, 0], 1, 200, -1, 1,
                [old.Id, 13, first.Id, 17, second.Id, 19, future.Id, 23])], []);
        var policy = new DungeonResourceDropPolicy(rules, candidates,
            id => new(id, "monster/合成星空.mob", [new(old.Id, 1)], null), id => templates[id], special);
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 2 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 200000 400000 600000 800000 900000 990000 1000000
            [item drop ref table] 1 0 2
            """), candidates, new EpicPieceDropCatalog(new EpicPieceCatalog(
                "[piece list]\n9000 3000\n[/piece list]\n[equipment recipe info]\n[/equipment recipe info]"),
                id => id == old.Id ? old with { MinimumLevel = 1 } : templates[id]), 1000);
        var monster = new DungeonPlannedMonster(0, 10, 42, 1, null) { SpawnType = 3, HellRewardRolls = 2 };
        var normal = policy.PrepareEntry(entry, hell)(monster);
        var prepareLegend = policy.PrepareEntry(entry with { IsHellLegend = true }, hell);
        var legend = prepareLegend(monster);
        var pools = legend.Trials.SelectMany(trial => trial.Items is { } direct ? new[] { direct } :
            trial.RarityPools.IsDefaultOrEmpty ? [] : trial.RarityPools.Select(row => row.Items));
        That(pools.SelectMany(pool => pool.Entries).Where(row => row.Template is { Kind: ItemKind.Equipment, Rarity: 4 })
            .Select(row => row.Template.Id).Distinct().Order().SequenceEqual([first.Id, second.Id]) &&
            normal.Trials[2].RarityPools[4].Items.Entries.Any(row => row.Template.Id == old.Id),
            "星空按装备最低等级限制所有史诗通道，普通深渊仍保留90级；不把grade相同误当同装备等级");
        That(legend.Trials[4].Items!.TotalWeight == 0 && legend.Trials[5].Items!.Entries.Select(row => row.CumulativeWeight).SequenceEqual([17, 36]) &&
            legend.Trials[2].RarityPools[0].Items.Entries.Single().Template.Id == ordinary.Id,
            "物品专项不借低等级补位，独立候选保留17/19相对权重，非史诗通道仍保留普通装备");
        That(legend.Trials.Length == normal.Trials.Length - 1 && legend.Trials[^1].Attempts == 2 &&
            normal.Trials[^1].Items!.Entries.Single().Template.Id == material.Id &&
            legend.Trials.Zip(normal.Trials).All(pair => pair.First.Threshold == pair.Second.Threshold &&
                pair.First.ProbabilityDenominator == pair.Second.ProbabilityDenominator && pair.First.Attempts == pair.Second.Attempts &&
                pair.First.GrowthContractEligible == pair.Second.GrowthContractEligible),
            "星空禁用旧史诗碎片实验奖励，装备/特殊通道概率、抽样次数与成长契约标记保留");
        That(ReferenceEquals(legend, prepareLegend(monster with { ObjectKey = 11 })),
            "星空过滤后的不可变计划仍按同次入图复用，不在每次死亡重建候选");
        var missing = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([old]),
            id => new(id, "monster/合成缺95级池.mob", null, null), _ => old)
            .PrepareEntry(entry with { IsHellLegend = true })(monster with { HellRewardRolls = 0 });
        That(missing.Trials[2].RarityPools[4].Items.TotalWeight == 0,
            "资源没有95级史诗时保持空候选，不退回90级或其它稀有度制造奖励");
        var normalOnly = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([ordinary]),
            id => new(id, "monster/合成仅普通池.mob", null, null), _ => ordinary)
            .PrepareEntry(entry with { IsHellLegend = true })(monster with { HellRewardRolls = 0 });
        That(normalOnly.Trials[2].RarityPools[4].Items.TotalWeight == 0 && normalOnly.Trials[2].RarityPools[0].Items.TotalWeight > 0,
            "史诗通道没有任何史诗时，也不能沿普通参考池的白装回退伪造一次史诗命中");
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
