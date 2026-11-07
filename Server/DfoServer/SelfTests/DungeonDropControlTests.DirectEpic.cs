using System.Collections.Immutable;
using DfoServer.Game.Adventure;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonDropControlTests
{
    static void VerifyDirectEpics()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var material = new ItemTemplate(1600, ItemKind.Material, 0, 1, 0, 99, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "直接掉落材料");
        var firstEpic = new ItemTemplate(1601, ItemKind.Equipment, 0, 1, 10, 1, 1, 4, 1,
            "[free]", ushort.MaxValue, 0, "直接掉落史诗甲");
        var secondEpic = firstEpic with { Id = 1602, Name = "直接掉落史诗乙" };
        var mixed = new DungeonItemDropPool([new DungeonWeightedDropItem(material, 6),
            new(firstEpic, 1), new(secondEpic, 3)]);
        var counts = ImmutableDictionary<int, int>.Empty.Add(material.Id, 5).Add(firstEpic.Id, 2).Add(secondEpic.Id, 3);

        foreach (var category in new[] { DungeonDropCategory.Special, DungeonDropCategory.MonsterItems })
        {
            var source = new DungeonDropGenerationPlan("直接史诗混池边界", [new(100, mixed, 1, 1,
                ProbabilityDenominator: 1000, ItemCounts: counts, Category: category)]);
            foreach (var (factor, firstWidth, secondWidth) in new (decimal, long, long)[]
                { (0, 0, 0), (1, 100, 300), (2, 200, 600), (100, 2050, 6150) })
            {
                var rates = new DungeonDropMultipliers(Equipment: factor, MonsterItems: 3, Special: 3);
                var controlled = DungeonDropControlSnapshot.Apply(source, rates);
                // 材料始终18%；史诗原为1%和3%，封顶时按1:3瓜分剩余82%。
                CheckRange(controlled, 1000, 10, 0, 1800, material.Id, 5);
                CheckRange(controlled, 1000, 10, 1800, 1800 + firstWidth, firstEpic.Id, 2);
                CheckRange(controlled, 1000, 10, 1800 + firstWidth, 1800 + firstWidth + secondWidth, secondEpic.Id, 3);
                CheckRange(controlled, 1000, 10, 1800 + firstWidth + secondWidth, 10000, 0, 0);
                That(controlled.MaximumObjects == 3 && controlled.Trials.Length == 1 &&
                    controlled.Trials[0].Attempts == 1 && controlled.Trials[0].ItemCounts == counts,
                    "直接混池保持原分支数量及完整预留，倍率不拆成多个独立掉落");

                var rows = DungeonDropListing.ComparisonRows(source, rates).ToArray();
                That(rows.Length == 3 && rows.Select(row => row.ItemId).SequenceEqual(new[] { 1600, 1601, 1602 }) &&
                    rows[0].OriginalProbability == 0.06m && rows[1].OriginalProbability == 0.01m &&
                    rows[2].OriginalProbability == 0.03m,
                    "混池预览保留原始逐候选概率与顺序，零倍率行不消失");
                That(rows[0].Multiplier == 3 && rows[1].Multiplier == factor && rows[2].Multiplier == factor &&
                    rows[0].Probability == 0.18m && rows[1].Probability == firstWidth / 10000m &&
                    rows[2].Probability == secondWidth / 10000m,
                    "混池预览按真实候选回显装备或来源倍率，并与生成边界一致");
            }

            var unchanged = DungeonDropControlSnapshot.Apply(source, new());
            foreach (var (trigger, selection) in new[] { (0, 0), (99, 6), (99, 9), (100, 0) })
            {
                var originalBounds = new List<int>();
                var unchangedBounds = new List<int>();
                var original = GenerateAt(source, trigger * 10L + selection, 1000, 10, originalBounds);
                var actual = GenerateAt(unchanged, trigger * 10L + selection, 1000, 10, unchangedBounds);
                That(actual.SequenceEqual(original) && unchangedBounds.SequenceEqual(originalBounds),
                    "所有倍率为1时沿原生成器抽样顺序，不重抽或增加装备骰");
            }
        }

        var epicPool = new DungeonItemDropPool([new DungeonWeightedDropItem(firstEpic, 1)]);
        foreach (var category in new[] { DungeonDropCategory.Special, DungeonDropCategory.MonsterItems, DungeonDropCategory.Equipment })
        {
            var source = new DungeonDropGenerationPlan("纯史诗来源接管", [new(100, epicPool, 1, 1,
                ProbabilityDenominator: 1000, Category: category)]);
            var rates = new DungeonDropMultipliers(Equipment: 2, MonsterItems: 0, Special: 0);
            var controlled = DungeonDropControlSnapshot.Apply(source, rates);
            That(GenerateAt(controlled, 199, 1000, 1) is [{ Item.ItemId: 1601 }] &&
                GenerateAt(controlled, 200, 1000, 1).IsEmpty,
                "纯史诗只用装备倍率，不叠零来源倍率，原装备通道也不重复加倍");
            var row = DungeonDropListing.ComparisonRows(source, rates).Single();
            That(row.Multiplier == 2 && row.OriginalProbability == 0.1m && row.Probability == 0.2m,
                "纯史诗预览回显装备倍率，不冒用Special或MonsterItems");
        }

        var pair = new DungeonItemDropPool([new DungeonWeightedDropItem(material, 1), new(firstEpic, 1)]);
        var tiny = DungeonDropControlSnapshot.Apply(new("百万分母直接史诗", [new(1, pair, 1, 1,
            ProbabilityDenominator: 1000000)]), new(Equipment: 2));
        CheckRange(tiny, 1000000, 2, 0, 1, material.Id, 1);
        CheckRange(tiny, 1000000, 2, 1, 3, firstEpic.Id, 1);
        CheckRange(tiny, 1000000, 2, 3, 2000000, 0, 0);
        That(DungeonDropListing.Rows(tiny).Single(row => row.ItemId == firstEpic.Id).Probability == 0.000001m,
            "百万分母的小概率扩展保留新增一格，不压缩到万分比归零");

        var fractional = DungeonDropControlSnapshot.Apply(new("小数倍率后成长契约", [new(1, pair, 1, 1,
            ProbabilityDenominator: 1000000, GrowthContractEligible: true)]), new(Equipment: 0.5m))
            .WithGrowthContract(100);
        CheckRange(fractional, 1000000, 2, 0, 2, material.Id, 1);
        CheckRange(fractional, 1000000, 2, 2, 3, firstEpic.Id, 1);
        CheckRange(fractional, 1000000, 2, 3, 2000000, 0, 0);

        var lowerEquipment = firstEpic with { Id = 1603, Rarity = 2 };
        var equipmentMix = new DungeonItemDropPool([new DungeonWeightedDropItem(lowerEquipment, 1), new(firstEpic, 1)]);
        var equipmentControl = DungeonDropControlSnapshot.Apply(new("混池其它品质装备", [new(100, equipmentMix, 1, 1,
            ProbabilityDenominator: 1000, Category: DungeonDropCategory.MonsterItems)]), new(Equipment: 0, MonsterItems: 3));
        CheckRange(equipmentControl, 1000, 2, 0, 300, lowerEquipment.Id, 1);
        CheckRange(equipmentControl, 1000, 2, 300, 2000, 0, 0);

        var antonium = material with { Id = AdventureBenefitPolicy.AntoniumItemId, Rarity = 1 };
        var bonusPool = new DungeonItemDropPool([new DungeonWeightedDropItem(antonium, 1), new(firstEpic, 1)]);
        var frozenRates = new DungeonDropMultipliers(Equipment: 2, ExcludeLowEquipment: true, ExcludeLowMaterials: true);
        var frozen = DungeonDropControlSnapshot.Apply(new("混池后续加成", [new(100, bonusPool, 1, 1,
            ProbabilityDenominator: 1000, GrowthContractEligible: true)]), frozenRates);
        var growth = frozen.WithGrowthContract(100);
        var combined = growth.WithAntoniumDropBonus(10000).WithDisjointRestrictions(ImmutableHashSet.Create(4)).WithEpicDropBonus(100);
        // 原史诗10%，成长契约后20%，再加魔能石后40%；低品质材料仍冻结为空。
        CheckRange(frozen, 1000, 2, 100, 300, firstEpic.Id, 1);
        CheckRange(growth, 1000, 2, 200, 600, firstEpic.Id, 1);
        CheckRange(combined, 1000, 2, 400, 1200, firstEpic.Id, 1);
        CheckRange(combined, 1000, 2, 1200, 2000, 0, 0);
        var filteredBounds = new List<int>();
        That(GenerateAt(combined, 399, 1000, 2, filteredBounds).IsEmpty &&
            filteredBounds.SequenceEqual(new[] { 1000, 2 }),
            "后续加成仍保留低品质过滤，过滤材料后不补抽史诗");
        That(GenerateAt(combined, 400, 1000, 2) is [{ Item.DisjointForbidden: true }] &&
            combined.ExcludeLowEquipment && combined.ExcludeLowMaterials && combined.DropMultipliers == frozenRates &&
            frozen.Trials[0].Threshold == 100 && growth.Trials[0].Threshold == 200 && combined.Trials[0].Threshold == 400,
            "契约、魔能石、禁分解与药剂衍生保留冻结快照，原计划不被后续加成改写");
        var combinedRows = DungeonDropListing.Rows(combined).ToArray();
        That(combinedRows[0].Probability == 0.2m && combinedRows[1].Probability == 0.4m,
            "后续加成的混池清单使用最终阈值与史诗概率");

        var expired = firstEpic with { Expiration = new(AbsoluteTime: 999) };
        var expiredPool = new DungeonItemDropPool([new DungeonWeightedDropItem(material, 1), new(expired, 1)]);
        var expiredPlan = DungeonDropControlSnapshot.Apply(new("过期直接史诗", [new(100, expiredPool, 1, 1,
            ProbabilityDenominator: 1000)]), new(Equipment: 2));
        var expiredBounds = new List<int>();
        That(GenerateAt(expiredPlan, 100, 1000, 2, expiredBounds).IsEmpty &&
            expiredBounds.SequenceEqual(new[] { 1000, 2 }),
            "过期史诗命中冻结为空，不续期、重抽或借放大补发");

        var zeroSource = DungeonDropControlSnapshot.Apply(new("零原始史诗", [new(0, pair, 1, 1,
            ProbabilityDenominator: 1000)]), new(Equipment: 100, Special: 3));
        CheckRange(zeroSource, 1000, 2, 0, 2000, 0, 0);

        ImmutableArray<DungeonGroundContent> GenerateAt(DungeonDropGenerationPlan plan, long joint,
            int denominator, int totalWeight, List<int>? bounds = null)
        {
            int call = 0;
            return plan.Generate(bound =>
            {
                bounds?.Add(bound);
                if (call++ == 0)
                {
                    That(bound == denominator, "直接混池保留原声明的触发分母");
                    return checked((int)(joint / totalWeight));
                }
                if (call == 2)
                {
                    That(bound == totalWeight, "直接混池保留原声明的权重抽样");
                    return checked((int)(joint % totalWeight));
                }
                return 0;
            }, now);
        }

        void CheckRange(DungeonDropGenerationPlan plan, int denominator, int totalWeight,
            long start, long end, int itemId, int quantity)
        {
            if (start == end) return;
            foreach (long joint in new[] { start, start + (end - start) / 2, end - 1 }.Distinct())
            {
                var contents = GenerateAt(plan, joint, denominator, totalWeight);
                That(itemId == 0 ? contents.IsEmpty : !contents.IsEmpty &&
                    contents.All(content => content.Item?.ItemId == itemId) &&
                    contents.Sum(content => content.Item!.Count) == quantity,
                    "联合概率区间的起点、中点和末点真实生成预期分支及原数量");
                That(contents.Length <= plan.MaximumObjects,
                    "混池单次生成不超过原完整地面对象预留");
            }
        }
    }
}
