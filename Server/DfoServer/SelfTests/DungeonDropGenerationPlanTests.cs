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
        SourceRestrictions(stack, equipment);
        Console.WriteLine($"PASS {Count - before} 完整掉落生成数量、未命中及独立装备属性断言");
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
            "星空1271万分比6520将史诗绝对区间扩大为1.652倍，不改原计划");
        That(ReferenceEquals(original.WithEpicDropBonus(0, 10000), original),
            "零万分比加成复用原计划");
    }
}
