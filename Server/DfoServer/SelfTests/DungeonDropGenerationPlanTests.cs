using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonDropGenerationPlanTests
{
    public static void RunCore()
    {
        int before = Count;
        var stack = new ItemTemplate(1000, ItemKind.Consumable, 0, 1, 0, 3, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成数量模板");
        var pool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(stack, 1) });
        var plan = new DungeonDropGenerationPlan("明确合成概率/数量", [new(1000, pool, 7, 7)]);
        var result = plan.Generate(_ => 0, DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(plan.MaximumObjects == 3 && result.Select(asset => asset.Item!.Count).SequenceEqual(new[] { 3, 3, 1 }),
            "超单堆数量按原上限分成完整地面对象，总量不截断或另抽模板");
        var individual = new DungeonDropGenerationPlan("合成逐个落地", [new(10000, pool, 1, 7, IndividualObjects: true)]);
        That(individual.MaximumObjects == 7 && individual.Generate(bound => bound == 7 ? 6 : 0, DateTimeOffset.UnixEpoch)
            .Select(row => row.Item!.Count).SequenceEqual(Enumerable.Repeat(1, 7)), "可变数量一次抽完后逐个落地，完整容量按物品数预留");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("逐个超容量", [new(10000, pool, 501, 501, IndividualObjects: true)]),
            "材料逐个落地不能通过原堆叠上限绕过500对象容量");
        var miss = plan.Generate(bound => bound - 1, DateTimeOffset.UtcNow);
        That(miss.IsEmpty, "可控随机未命中保持零结果，不通过增加触发率确保掉落");
        var medal = stack with { Id = 100380021, Kind = ItemKind.GuildMedal, StackLimit = 1 };
        var medals = new DungeonDropGenerationPlan("公会勋章真实类别", [new(1000,
            new DungeonItemDropPool([new DungeonWeightedDropItem(medal, 1)]), 2, 2)]);
        That(medals.MaximumObjects == 2 && medals.Generate(_ => 0, DateTimeOffset.UtcNow) is [{ Item.Kind: ItemKind.GuildMedal, Item.Count: 1 },
                { Item.Kind: ItemKind.GuildMedal, Item.Count: 1 }] && medals.Generate(bound => bound - 1, DateTimeOffset.UtcNow).IsEmpty,
            "公会勋章通过已有单件核心逐个生成，未命中仍空，不能删掉真实候选或保证掉落");
        var equipment = stack with { Id = 2000, Kind = ItemKind.Equipment, StackLimit = 1, Durability = 41 };
        var equipmentPool = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(equipment, 7) });
        var gear = new DungeonDropGenerationPlan("明确合成装备数量", [new(1000, equipmentPool, 2, 2)]);
        int quality = 0;
        var generated = gear.Generate(bound => bound == int.MaxValue ? quality++ : 0, DateTimeOffset.UtcNow);
        That(generated.Length == 2 && generated[0].Item!.Value == 0 && generated[1].Item!.Value == 1 &&
            generated.All(asset => asset.Item!.Durability == 41), "多件装备分别创建品级种子和完整Core，不用一个实例重复授予");
        Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("容量fixture", [new(1000, equipmentPool, 501, 501)]),
            "合法通知无法容纳时拒绝完整声明，不能只保留500件");
        Reject<InvalidDataException>(() => plan.Generate(_ => -1, DateTimeOffset.UtcNow), "负抽样不取模或重新抽奖");
        var independent = new DungeonDropGenerationPlan("合成百万分母边界", [new(500000, pool, 1, 1, ProbabilityDenominator: 1000000)]);
        That(independent.Generate(bound => bound == 1000000 ? 499999 : 0, DateTimeOffset.UtcNow).Length == 1 &&
            independent.Generate(bound => bound == 1000000 ? 500000 : 0, DateTimeOffset.UtcNow).IsEmpty,
            "不同随机分母保留原临界值，百万分母不能被固定万分母截断");
        var deadline = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var expired = stack with { Id = 3000, Expiration = new(deadline.ToUnixTimeSeconds()) };
        var mixed = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(expired, 1), new(stack, 1) });
        var dated = new DungeonDropGenerationPlan("合成到期候选与合法奖励", [new(10000, mixed, 1, 1), new(10000, pool, 1, 1)]);
        int draws = 0;
        var afterExpiry = dated.Generate(_ => { draws++; return 0; }, deadline);
        That(afterExpiry.Length == 1 && afterExpiry.Single().Item!.ItemId == stack.Id && draws == 4 && mixed.TotalWeight == 2,
            "命中过期候选不重抽、不改权重，后续合法奖励仍完整生成");
        That(dated.Generate(_ => 0, deadline.AddSeconds(-1)).Select(row => row.Item!.ItemId).SequenceEqual(new[] { expired.Id, stack.Id }),
            "截止之前仍可生成有效活动物品，不能在启动或入图时永久删掉候选");
        var emptyPlan = new DungeonDropGenerationPlan("全部候选到期", [new(10000, mixed, 1, 1)]);
        That(emptyPlan.Generate(_ => 0, deadline.AddSeconds(1)).IsEmpty,
            "全部抽样到期可冻结空集合，不制造替代物或抛出连接异常");
        var relative = new DungeonItemDropPool(new DungeonWeightedDropItem[] { new(expired with { Expiration = new(deadline.ToUnixTimeSeconds(), Days: 1) }, 1) });
        var relativePlan = new DungeonDropGenerationPlan("相对期限优先", [new(10000, relative, 1, 1)]);
        That(relativePlan.Generate(_ => 0, deadline).Single().Item!.ExpireTime == deadline.ToUnixTimeSeconds() + 86400,
            "绝对日期已过但相对期限有效时不误删奖励");
        Reject<InvalidDataException>(() => DungeonDropCreation.Create(expired, 1, _ => 0, deadline),
            "底层创建仍拒绝过期资产，不借掉落容错延长其它入口期限");
        EpicDropBonus(stack, equipment);
        BossDropBonus(stack, equipment);
        SourceRestrictions(stack, equipment);
        LegacyHellEquipmentFilter(stack, equipment);
        Console.WriteLine($"PASS {Count - before} 完整掉落生成数量、未命中及独立装备属性断言");
    }

    static void BossDropBonus(ItemTemplate stack, ItemTemplate equipment)
    {
        var items = new DungeonItemDropPool([new DungeonWeightedDropItem(equipment, 2),
            new DungeonWeightedDropItem(stack, 3), new DungeonWeightedDropItem(equipment with { Id = 2001, Rarity = 4 }, 1)]);
        var gear = new DungeonItemDropPool([new DungeonWeightedDropItem(equipment, 1)]);
        var material = new DungeonItemDropPool([new DungeonWeightedDropItem(stack, 1)]);
        var direct = new DungeonDropTrial(5000, gear, 1, 1, Attempts: 2, Category: DungeonDropCategory.Equipment);
        var independent = new DungeonDropTrial(500000, gear, 1, 1, ProbabilityDenominator: 1000000, Attempts: 2)
            { IndependentDrop = true };
        var nonEquipment = independent with { Items = material };
        var mixed = independent with { Items = items, Threshold = 1000, ProbabilityDenominator = 10000, Attempts = 1 };
        var original = new DungeonDropGenerationPlan("领主及独立装备概率合成夹具",
            [direct, independent, nonEquipment, mixed, direct with { Threshold = 10000 }, direct with { Threshold = 0 }])
        {
            OrdinaryLukeDailySource = 42, ExcludeLowEquipment = true, ExcludeLowMaterials = true,
            DropMultipliers = new(Equipment: 2)
        };
        var boosted = original.WithBossDropBonus(1);
        var doubled = original.WithBossDropBonus(100);
        That(doubled.Trials[0].Threshold == 10000 &&
            doubled.Trials[1].BossDropAdjustment!.Widths(doubled.Trials[1])[0] == 1000000,
            "领主参数100表示原概率乘2，与史诗效果一致使用百分比相对增量");
        That(boosted.Trials[0].Threshold == 5050 && boosted.Trials[0].Attempts == 2 &&
            boosted.Trials[1].BossDropAdjustment!.Widths(boosted.Trials[1]).SequenceEqual(new long[] { 505000, 495000 }) &&
            ReferenceEquals(boosted.Trials[2], nonEquipment) && ReferenceEquals(boosted.Trials[4], original.Trials[4]) &&
            ReferenceEquals(boosted.Trials[5], original.Trials[5]),
            "领主1%为原概率乘1.01；百万分母独立装备同步生效，非装备独立掉落、必得和零概率不改变");
        That(boosted.MaximumObjects == original.MaximumObjects && boosted.ResourceSource == original.ResourceSource &&
            boosted.OrdinaryLukeDailySource == 42 && boosted.ExcludeLowEquipment && boosted.ExcludeLowMaterials &&
            boosted.DropMultipliers == original.DropMultipliers && original.Trials[0].Threshold == 5000 &&
            original.Trials[1].BossDropAdjustment is null,
            "领主加成不改原计划、预留对象数、来源、过滤、数量或独立抽选次数");
        That(boosted.Trials[3].BossDropAdjustment!.Widths(boosted.Trials[3]).SequenceEqual(new long[] { 2020, 3000, 1010, 53970 }),
            "独立混池只增加装备份额，材料保留原绝对概率与互斥选择");
        var plainMixed = new DungeonDropGenerationPlan("独立混池临界抽选", [mixed]);
        var hitMixed = plainMixed.WithBossDropBonus(1);
        int Sample(int bound) => bound == 10000 ? 1003 : bound == 6 ? 2 : 0;
        That(plainMixed.Generate(Sample, DateTimeOffset.UnixEpoch).IsEmpty &&
            hitMixed.Generate(Sample, DateTimeOffset.UnixEpoch) is [{ Item.ItemId: 2001 }] &&
            hitMixed.MaximumObjects == plainMixed.MaximumObjects,
            "独立装备新增概率来自原空抽空间，真实生成仍为一次试验的一件原候选");
        var adjustedMixed = mixed with { EpicAdjustment = new(1050),
            RarityBonusGroup = DungeonRarityBonusGroup.IndependentLottery,
            EquipmentRarityAdjustment = new(new(10, 20)) };
        var previous = new DungeonBossDropAdjustment(0).Widths(adjustedMixed);
        var combined = new DungeonDropGenerationPlan("既有史诗和装备加成叠加", [adjustedMixed]).WithBossDropBonus(1).Trials[0];
        var after = combined.BossDropAdjustment!.Widths(combined);
        That(after[1] == previous[1] && after[0] + after[2] == (previous[0] + previous[2]) * 101 / 100 &&
            after.Sum() == 60000 && combined.EpicAdjustment == adjustedMixed.EpicAdjustment &&
            combined.EquipmentRarityAdjustment == adjustedMixed.EquipmentRarityAdjustment,
            "领主增量在既有史诗和装备品质加成之后组合，不覆盖旧效果或放大材料");
        var capped = new DungeonDropGenerationPlan("独立混池封顶", [mixed]).WithBossDropBonus(int.MaxValue).Trials[0];
        var widths = capped.BossDropAdjustment!.Widths(capped);
        That(widths[1] == 3000 && widths[0] + widths[2] == 57000 && widths[^1] == 0 && widths.Sum() == 60000 &&
            new DungeonDropGenerationPlan("领主普通概率封顶", [direct]).WithBossDropBonus(int.MaxValue).Trials[0].Threshold == 10000,
            "大增量以原分母及材料原概率为界封顶，不能溢出或新增次数");
        That(ReferenceEquals(original.WithBossDropBonus(0), original) &&
            ReferenceEquals(new DungeonDropGenerationPlan("非装备独立物保持原样", [nonEquipment]).WithBossDropBonus(1).Trials[0], nonEquipment),
            "零加成及纯非装备独立池复用原规则");
        Reject<InvalidDataException>(() => original.WithBossDropBonus(-1), "负领主加成拒绝");
    }

    static void LegacyHellEquipmentFilter(ItemTemplate stack, ItemTemplate equipment)
    {
        DungeonDropTrial Drop(ItemTemplate item, bool filtered) => new DungeonDropTrial(10000,
            new DungeonItemDropPool([new DungeonWeightedDropItem(item, 1)]), 1, 1)
            { ExcludeLegacyHellLowEquipment = filtered };
        foreach (int rarity in new[] { 0, 1, 2, 3, 4 })
        foreach (bool sealedEquipment in new[] { false, true })
        {
            var item = equipment with { Rarity = rarity, RandomOptionEquipment = true,
                MagicSealedEquipment = sealedEquipment && (rarity is 2 or 3) };
            var plan = new DungeonDropGenerationPlan("旧地区额外装备过滤边界", [Drop(item, true)]);
            bool excluded = rarity is 0 or 1 || item.MagicSealedEquipment;
            var result = plan.Generate(_ => 0, DateTimeOffset.UnixEpoch);
            That(result.IsEmpty == excluded && (excluded || result.Single().Item!.ItemId == item.Id),
                "仅排除白蓝和精确魔封，标签存在不误删非魔封紫粉或史诗: " + rarity + "/" + sealedEquipment);
        }
        var blue = equipment with { Rarity = 1 };
        var material = stack with { Id = 3000, Kind = ItemKind.Material, StackLimit = 10 };
        var unrelated = new DungeonDropGenerationPlan("同怪不同来源隔离", [Drop(blue, true), Drop(blue, false),
            Drop(material, false), new(10000, null, 1, 1, new DungeonDropGoldReference(95, 100, 0))]);
        var otherDrops = unrelated.Generate(_ => 0, DateTimeOffset.UnixEpoch);
        That(otherDrops.Length == 3 && otherDrops[0].Item!.ItemId == blue.Id &&
            otherDrops[1].Item!.ItemId == material.Id && otherDrops[2].GoldAmount == 100,
            "同怪原普通装备、材料、金币保留，不扩大为整个怪物计划的低品质过滤");
        var purple = equipment with { Id = 2001, Rarity = 2 };
        var weighted = new DungeonItemDropPool([new DungeonWeightedDropItem(blue, 2), new DungeonWeightedDropItem(purple, 3)]);
        var mixed = new DungeonDropGenerationPlan("旧深渊原权重不重抽", [new DungeonDropTrial(10000, weighted, 1, 1)
            { ExcludeLegacyHellLowEquipment = true }]);
        for (int roll = 0; roll < 5; roll++)
        {
            int draws = 0;
            var result = mixed.Generate(bound => { draws++; return bound == 5 ? roll : 0; }, DateTimeOffset.UnixEpoch);
            That(weighted.TotalWeight == 5 && result.IsEmpty == (roll < 2) && draws == (roll < 2 ? 2 : 3) &&
                (result.IsEmpty || result.Single().Item!.ItemId == purple.Id),
                "原加权区间命中白蓝只为空，不删除池项、重抽或侵占紫装区间: " + roll);
        }
        var pools = Enumerable.Range(0, 7).Select(rarity => new DungeonDropRarityPool(
            rarity == 6 ? 1000000 : (rarity + 1) * 100000,
            new DungeonItemDropPool([new DungeonWeightedDropItem(equipment with { Id = 4000 + rarity, Rarity = rarity }, 1)])))
            .ToImmutableArray();
        var original = new DungeonDropGenerationPlan("旧深渊派生标记保留", [new DungeonDropTrial(1000, null, 1, 1,
            RarityPools: pools, GrowthContractEligible: true, Category: DungeonDropCategory.Equipment)
            { RarityBonusGroup = DungeonRarityBonusGroup.HellEquipment, ExcludeLegacyHellLowEquipment = true }]);
        var derived = new[] { original.WithGrowthContract(10), original.WithEpicDropBonus(100),
            original.WithEquipmentRarityBonus(new(10, 10)), DungeonDropControlSnapshot.Apply(original, new(Equipment: 2)),
            original.WithDisjointRestrictions(ImmutableHashSet.Create(4)) };
        That(derived.All(plan => !ReferenceEquals(plan, original) && plan.Trials.Single().ExcludeLegacyHellLowEquipment &&
            plan.Trials.Single().RarityBonusGroup == DungeonRarityBonusGroup.HellEquipment),
            "成长、史诗药剂、稀有度加成、掉落控制和分解限制派生不丢额外装备过滤");
        That(original.Trials.Single().RarityPools.Select(pool => pool.Threshold).SequenceEqual(pools.Select(pool => pool.Threshold)),
            "派生处理不改写已冻结的原始稀有度阈值");
    }

    static void SourceRestrictions(ItemTemplate stack, ItemTemplate equipment)
    {
        var epic = equipment with { Rarity = 4 };
        var rare = equipment with { Id = 2001, Rarity = 2 };
        var material = stack with { Rarity = 4 };
        DungeonDropTrial Drop(ItemTemplate item, int count = 1) => new(10000,
            new DungeonItemDropPool([new DungeonWeightedDropItem(item, 1)]), count, count, GrowthContractEligible: true);
        var original = new DungeonDropGenerationPlan("星河裂缝来源限制合成夹具",
            [Drop(epic, 2), Drop(rare), Drop(material), new(10000, null, 1, 1, new DungeonDropGoldReference(95, 100, 0))]);
        var restricted = original.WithDisjointRestrictions(ImmutableHashSet.Create(4));
        int draws = 0, normalDraws = 0;
        var drops = restricted.WithGrowthContract(10).WithEpicDropBonus(100)
            .Generate(_ => { draws++; return 0; }, DateTimeOffset.UnixEpoch);
        var normal = original.WithGrowthContract(10).WithEpicDropBonus(100)
            .Generate(_ => { normalDraws++; return 0; }, DateTimeOffset.UnixEpoch);
        That(drops.Take(2).All(row => row.Item!.DisjointForbidden) &&
            drops.Skip(2).Where(row => !row.IsGold).All(row => !row.Item!.DisjointForbidden),
            "活动来源只限制声明稀有度的装备，多件逐一标记，材料和金币不附加限制");
        That(normal.Where(row => !row.IsGold).All(row => !row.Item!.DisjointForbidden) && !epic.DisjointForbidden &&
            original.MaximumObjects == restricted.MaximumObjects && draws == normalDraws &&
            normal.Zip(drops).All(pair => pair.First.IsGold ? pair.First == pair.Second :
                pair.First.Item == pair.Second.Item! with { DisjointForbidden = false }),
            "来源限制不改共享模板、普通掉落、数量、品质、金币或抽样次数，加成后仍保留");
        var restored = System.Text.Json.JsonSerializer.Deserialize<DfoServer.Game.Inventory.Models.ItemCore>(
            System.Text.Json.JsonSerializer.Serialize(drops[0].Item));
        That(restored == drops[0].Item && restored!.DisjointForbidden && !restored.DestructionSoulForbidden,
            "活动史诗来源限制沿已有Core序列化恢复，不借用未知客户端字段或禁止出售");
        var filtered = new DungeonDropGenerationPlan("活动来源保留玩家掉落过滤",
            [Drop(equipment with { Rarity = 0 }), Drop(stack with { Kind = ItemKind.Material, Rarity = 0 })])
            { ExcludeLowEquipment = true, ExcludeLowMaterials = true };
        That(filtered.Generate(_ => 0, DateTimeOffset.UnixEpoch).IsEmpty &&
            filtered.WithDisjointRestrictions(ImmutableHashSet.Create(4)).Generate(_ => 0, DateTimeOffset.UnixEpoch).IsEmpty,
            "附加活动禁分解来源不恢复玩家已过滤的低稀有度装备和材料");
    }

    static void EpicDropBonus(ItemTemplate stack, ItemTemplate equipment)
    {
        var pools = Enumerable.Range(0, 7).Select(index => new DungeonItemDropPool([
            new DungeonWeightedDropItem(equipment with { Id = 5000 + index, Rarity = index }, 1)])).ToArray();
        int[] thresholds = [100000, 300000, 500000, 700000, 800000, 900000, 1000000];
        var rarity = thresholds.Select((value, index) => new DungeonDropRarityPool(value, pools[index])).ToImmutableArray();
        var alternate = new DungeonItemDropPool([new DungeonWeightedDropItem(equipment with { Id = 6004, Rarity = 4 }, 1)]);
        rarity = rarity.SetItem(4, rarity[4] with { AlternateItems = alternate, AlternatePermille = 250,
            SecondaryItems = pools[4], SecondaryPermille = 125 });
        var replacement = new DungeonItemDropPool([new DungeonWeightedDropItem(equipment with { Id = 7004, Rarity = 4 }, 1)]);
        var trial = new DungeonDropTrial(1234, null, 1, 1, RarityPools: rarity, Attempts: 2,
            GrowthContractEligible: true, EpicReplacementItems: replacement);
        var guaranteed = new DungeonDropTrial(10000, pools[4], 1, 1);
        var gold = new DungeonDropTrial(10000, null, 1, 1, new DungeonDropGoldReference(95, 100, 0));
        var fragment = new DungeonDropTrial(10000, new DungeonItemDropPool([new DungeonWeightedDropItem(stack, 1)]), 1, 1);
        var original = new DungeonDropGenerationPlan("明确合成固定史诗药剂策略", [trial, guaranteed, gold, fragment])
            { OrdinaryLukeDailySource = 5004 };
        var doubled = original.WithEpicDropBonus(100);
        int[] expected = [88888, 266666, 444444, 622222, 822222, 911111, 1000000];
        That(doubled.Trials[0].RarityPools.Select(row => row.Threshold).SequenceEqual(expected) &&
            doubled.Trials[0].RarityPools[4].Threshold - doubled.Trials[0].RarityPools[3].Threshold == 200000 &&
            original.Trials[0].RarityPools.Select(row => row.Threshold).SequenceEqual(thresholds),
            "固定100加成将史诗绝对区间翻倍，非史诗按原比例累计取整且不改原计划");
        That(doubled.Trials[0] with { RarityPools = trial.RarityPools } == trial &&
            doubled.Trials.Skip(1).SequenceEqual(original.Trials.Skip(1)) && doubled.MaximumObjects == original.MaximumObjects &&
            doubled.ResourceSource == original.ResourceSource && doubled.OrdinaryLukeDailySource == 5004 &&
            doubled.ReferenceQuality == original.ReferenceQuality && ReferenceEquals(doubled.Amplification, original.Amplification),
            "只改稀有度阈值；原触发率、次数、条件分支、池、史诗替换、容量和卢克来源完整保留");
        for (int index = 0; index < 7; index++)
            That(ReferenceEquals(doubled.Trials[0].RarityPools[index].Items, rarity[index].Items) &&
                doubled.Trials[0].RarityPools[index].AlternatePermille == rarity[index].AlternatePermille &&
                doubled.Trials[0].RarityPools[index].SecondaryPermille == rarity[index].SecondaryPermille,
                "固定史诗加成不改池内部权重和分支概率");
        var simple = new DungeonDropGenerationPlan("合成稀有度边界", [trial with { Attempts = 1, RarityPools =
            thresholds.Select((value, index) => new DungeonDropRarityPool(value, pools[index])).ToImmutableArray(), EpicReplacementItems = null }]);
        var enhanced = simple.WithEpicDropBonus(100);
        int ItemAt(DungeonDropGenerationPlan plan, int roll, out int draws)
        {
            int calls = 0;
            int id = plan.Generate(bound => { calls++; return bound == 1000000 ? roll : 0; }, DateTimeOffset.UnixEpoch).Single().Item!.ItemId;
            draws = calls;
            return id;
        }
        That(ItemAt(simple, 650000, out int beforeDraws) == 5003 && ItemAt(enhanced, 650000, out int afterDraws) == 5004 &&
            beforeDraws == afterDraws && ItemAt(enhanced, 822221, out _) == 5004 && ItemAt(enhanced, 822222, out _) == 5005,
            "真实生成在扩大后的史诗区间边界变化，保持相同抽样次数且上界仍排他");
        var capped = original.WithEpicDropBonus(int.MaxValue);
        That(capped.Trials[0].RarityPools.Select(row => row.Threshold).SequenceEqual(new[] { 0, 0, 0, 0, 1000000, 1000000, 1000000 }) &&
            capped.Trials[0].Threshold == 1234 && capped.Trials[0].Attempts == 2 && capped.MaximumObjects == original.MaximumObjects,
            "巨大加成以原稀有度分母封顶，不能溢出、增加触发或额外抽奖");
        That(ReferenceEquals(original.WithEpicDropBonus(0), original) &&
            ReferenceEquals(new DungeonDropGenerationPlan("直接奖励不加成", [guaranteed, gold, fragment]).WithEpicDropBonus(100).Trials[0], guaranteed) &&
            ReferenceEquals(new DungeonDropGenerationPlan("非完整七池不扩展", [trial with { RarityPools = rarity.RemoveAt(0) }])
                .WithEpicDropBonus(100).Trials[0].RarityPools[0], rarity[1]),
            "零加成复用原计划，直接史诗、金币、碎片和非完整七池不受药剂改变");
        Reject<InvalidDataException>(() => original.WithEpicDropBonus(-1), "负史诗加成不静默改成零或反向扣减");
        Reject<InvalidDataException>(() => original.WithEpicDropBonus(6520, 0), "零加成分母拒绝");
        Reject<InvalidDataException>(() => original.WithEpicDropBonus(6520, -10000), "负加成分母拒绝");
        var permyriad = original.WithEpicDropBonus(6520, 10000);
        That(permyriad.Trials[0].RarityPools[4].Threshold - permyriad.Trials[0].RarityPools[3].Threshold == 165200 &&
            permyriad.Trials[0].RarityPools[^1].Threshold == 1000000 &&
            original.Trials[0].RarityPools[4].Threshold - original.Trials[0].RarityPools[3].Threshold == 100000,
            "通用比例工具仍按明确分母计算，不从Buff编号推断单位");
        That(ReferenceEquals(original.WithEpicDropBonus(0, 10000), original),
            "零万分比加成复用原计划");
        var ordinary = trial with { RarityBonusGroup = DungeonRarityBonusGroup.OrdinaryEquipment };
        var hell = trial with { RarityBonusGroup = DungeonRarityBonusGroup.HellEquipment };
        var mixed = new DungeonDropGenerationPlan("小灯笼只影响深渊结算装备", [ordinary, hell, guaranteed, gold, fragment]);
        foreach (int percent in new[] { 10, 5 })
        {
            var lantern = mixed.WithEpicDropBonus(percent, hellRewardOnly: true);
            var adjusted = lantern.Trials[1];
            That(adjusted.RarityPools[4].Threshold - adjusted.RarityPools[3].Threshold == 100000 + percent * 1000 &&
                adjusted.RarityPools[^1].Threshold == 1000000 && adjusted.Threshold == hell.Threshold && adjusted.Attempts == hell.Attempts &&
                adjusted.EpicReplacementItems == hell.EpicReplacementItems && lantern.MaximumObjects == mixed.MaximumObjects &&
                ReferenceEquals(lantern.Trials[0], ordinary) && ReferenceEquals(lantern.Trials[2], guaranteed) &&
                ReferenceEquals(lantern.Trials[3], gold) && ReferenceEquals(lantern.Trials[4], fragment) &&
                hell.RarityPools[4].Threshold - hell.RarityPools[3].Threshold == 100000,
                "小灯笼10/5只扩大深渊史诗区间，不改普通装备、触发次数、金币、碎片或替换池");
        }
        That(ReferenceEquals(original.WithEpicDropBonus(10, hellRewardOnly: true), original),
            "没有正式深渊装备来源的计划不因小灯笼产生奖励或改变普通掉落");
        var directEpic = new DungeonDropTrial(1000, pools[4], 1, 1, Category: DungeonDropCategory.Equipment);
        var directPlan = new DungeonDropGenerationPlan("黎明纯史诗独立命中", [directEpic, fragment]);
        var directBonus = directPlan.WithEpicDropBonus(1250,100,includeDirectEpic:true);
        That(directBonus.Trials[0].Threshold==10000 && directBonus.Trials[0].Attempts==1 &&
            directBonus.Trials[0].Items==directEpic.Items && ReferenceEquals(directBonus.Trials[1],fragment) &&
            directEpic.Threshold==1000,"次数型多模式药剂覆盖黎明直接史诗池的原命中概率，不增加件数或碎片");
        var mixedItems=new DungeonItemDropPool([new DungeonWeightedDropItem(equipment with { Rarity=1 },1),
            new DungeonWeightedDropItem(equipment with { Id=9004,Rarity=4 },1)]);
        var directMixed=directEpic with { Items=mixedItems,Threshold=10,ProbabilityDenominator=100 };
        var mixedBonus=new DungeonDropGenerationPlan("直接装备混池",[directMixed]).WithEpicDropBonus(2500,100,includeDirectEpic:true);
        That(mixedBonus.Trials[0].EpicAdjustment!.Widths(mixedBonus.Trials[0]).SequenceEqual(new long[] {10,190}) &&
            mixedBonus.Trials[0].Threshold==10 && directMixed.EpicAdjustment is null,
            "直接混池沿原联合整数网格增加史诗份额，非史诗绝对概率与原计划不变");
        That(original.WithEpicDropBonus(6520, HellLegendEpicDropBuffDefinition.BonusScale).Trials[0].RarityPools[4].Threshold == 1000000 &&
            HellLegendEpicDropBuffDefinition.BonusScale == 100, "星空6520也采用百分比并以原史诗区间上限封顶");
        foreach (var (value,expectedWidth) in new[] { (2500,1000000),(500,600000),(1250,1000000) })
        {
            var match=original.WithEpicDropBonus(value,DungeonEpicDropBuffPolicy.BonusScale,includeDirectEpic:true);
            That(match.Trials[0].RarityPools[4].Threshold-match.Trials[0].RarityPools[3].Threshold==expectedWidth &&
                match.Trials[0].Threshold==trial.Threshold && match.Trials[0].Attempts==trial.Attempts,
                "280/284全部按百分比语义增加2500%/500%/1250%并封顶，不增加骰数");
        }
    }
}
