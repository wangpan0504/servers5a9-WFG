using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonDropControlTests
{
    static void VerifyEquipmentAndFilters()
    {
        var template = new ItemTemplate(1200, ItemKind.Equipment, 0, 1, 10, 1, 1, 4, 1, "[free]", ushort.MaxValue, 0, "史诗");
        DungeonItemDropPool Pool(ItemTemplate item) => new([new DungeonWeightedDropItem(item, 1)]);
        var epic = Pool(template);
        var thresholds = new[] { 200000, 400000, 600000, 800000, 900000, 950000, 1000000 };
        var pools = thresholds.Select((threshold, index) => new DungeonDropRarityPool(threshold,
            Pool(template with { Id = 1200 + index, Rarity = index }))).ToImmutableArray();
        var source = new DungeonDropGenerationPlan("hell-boundary", [new(750, null, 1, 1,
            ProbabilityDenominator: 1000, RarityPools: pools, Category: DungeonDropCategory.Equipment)]);
        decimal EpicProbability(DungeonDropGenerationPlan plan) => DungeonDropListing.Rows(plan).Where(row => row.ItemId == 1204).Sum(row => row.Probability);
        That(EpicProbability(source) == 0.075m, "深渊原概率等于装备触发与史诗区间联合概率");
        foreach (decimal multiplier in new decimal[] { 0, 0.5m, 1, 2, 5, 20, 100 })
        {
            var adjusted = DungeonDropControlSnapshot.Apply(source, new(Equipment: multiplier));
            That(Math.Abs(EpicProbability(adjusted) - Math.Min(1m, 0.075m * multiplier)) <= 0.000001m,
                "装备倍率影响实际史诗概率，外层封顶后继续进入史诗区间");
            That(adjusted.Trials[0].Attempts == 1 && adjusted.Trials[0].MaximumCount == 1,
                "概率调整不偷偷增加抽样次数或掉落数量");
        }
        var white = new DungeonDropGenerationPlan("white-boss", [source.Trials[0] with { EpicReplacementItems = epic }]);
        var whiteAdjusted = DungeonDropControlSnapshot.Apply(white, new(Equipment: 2));
        That(ReferenceEquals(whiteAdjusted.Trials[0].EpicReplacementItems, epic), "白斯替换池在概率调整后保持身份");
        That(EpicProbability(DungeonDropControlSnapshot.Apply(source, new(Special: 100))) == EpicProbability(source),
            "深渊装备不再误用玩法材料倍率");
        var actualHell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 90 90 335 875 1000 1400 1000
            [basis of rarity dicision] 1 0 599000 993000 993000 1000000 1000001 1000001
            [item drop ref table] 90 6 3
            """), DungeonItemDropCatalog.Create([template with { MinimumLevel = 90, CreationRate = 1 }]));
        var actual = actualHell.Extend(new DungeonDropGenerationPlan("hell-entry", []), 90, 1, 1);
        var actualTwice = DungeonDropControlSnapshot.Apply(actual, new(Equipment: 2));
        That(actual.Trials.Single().Category == DungeonDropCategory.Equipment &&
            actualTwice.Generate(bound => bound == 1000000 ? 990000 : 0, DateTimeOffset.UnixEpoch) is [{ Item.ItemId: 1200 }] &&
            actual.Generate(bound => bound == 1000000 ? 990000 : 0, DateTimeOffset.UnixEpoch).IsEmpty,
            "真实深渊奖励策略追加的装备骰接入倍率，并在新增区间真实生成史诗");
        var now = DateTimeOffset.UnixEpoch;
        foreach (int rarity in Enumerable.Range(0, 7))
        {
            var item = template with { Id = 1300 + rarity, Rarity = rarity };
            var plan = new DungeonDropGenerationPlan("quality-filter", [new(1000, Pool(item), 1, 1, ProbabilityDenominator: 1000)]);
            var adjusted = DungeonDropControlSnapshot.Apply(plan, new(ExcludeLowEquipment: true, ExcludeLowMaterials: true));
            That(adjusted.Generate(_ => 0, now).IsEmpty == (rarity <= 3), "只过滤白蓝紫粉装备，保留史诗等其它品质");
        }
        foreach (var kind in new[] { ItemKind.Material, ItemKind.Consumable, ItemKind.Quest })
        foreach (int rarity in new[] { 0, 1, 4 })
        {
            var item = template with { Id = 1400 + rarity, Kind = kind, Rarity = rarity, Durability = 0, StackLimit = 99 };
            var plan = new DungeonDropGenerationPlan("material-filter", [new(1000, Pool(item), 2, 2, ProbabilityDenominator: 1000)]);
            var adjusted = DungeonDropControlSnapshot.Apply(plan, new(ExcludeLowMaterials: true));
            That(adjusted.Generate(_ => 0, now).IsEmpty == (kind == ItemKind.Material && rarity <= 1),
                "白蓝材料过滤不误删药品、任务物品及高品质碎片");
            That(adjusted.WithGrowthContract(50).WithEpicDropBonus(50).ExcludeLowMaterials,
                "契约和史诗药剂衍生计划保留过滤快照");
        }
        int draws = 0;
        var mixed = new DungeonDropGenerationPlan("no-reroll", [new(1000, new DungeonItemDropPool([
            new DungeonWeightedDropItem(template with { Rarity = 0 }, 1), new DungeonWeightedDropItem(template with { Rarity = 4 }, 1)]), 1, 1, ProbabilityDenominator: 1000)]);
        That(DungeonDropControlSnapshot.Apply(mixed, new(ExcludeLowEquipment: true)).Generate(_ => { draws++; return 0; }, now).IsEmpty && draws == 2,
            "过滤结果冻结为空，不重抽或将垃圾变为史诗");
    }
}
