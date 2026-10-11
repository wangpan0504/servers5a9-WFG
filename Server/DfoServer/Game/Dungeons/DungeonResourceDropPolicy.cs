using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 当前PVF提供候选、权重与概率，本服采用90参考业务算法；不声称是原版抽样。
internal sealed class DungeonResourceDropPolicy(DungeonDropRuleCatalog rules, DungeonItemDropCatalog candidates,
    Func<int, DungeonMonsterDropDeclaration> resolveMonster, Func<int, ItemTemplate> resolveItem, DungeonSpecialDropCatalog? special = null)
{
    public const string Rule = "pvf-monster-reference-v1";
    public static ImmutableArray<string> RetainedFields { get; } = ["drop prob[4]", "rarity rows[1..3]", "party member drop bonusrate",
        "dungeon difficulty drop bonusrate", "first boss/named mob hunting", "condition rate", "gold quantity", "gold volume", "item drop rarity control",
        "common champion drop item", "monster repeated item blocks after first"];
    static readonly decimal[] DifficultyRates = [1m, 1.2m, 1.4m, 1.6m, 1.8m];

    public DungeonDropGenerationPlan Prepare(int monsterId, int level, byte monsterType, byte difficulty, int? dungeonLevel = null)
    {
        if (monsterType > 3 || difficulty > 4)
            throw new InvalidDataException("资源掉落准备的怪物类型或难度越界");
        var probability = rules.ProbabilityRows.SingleOrDefault(row => level >= row.MinimumLevel && level <= row.MaximumLevel)
            ?? throw new NotSupportedException("当前怪物等级缺少PVF掉落概率段: " + level);
        var reference = rules.ItemReferences.SingleOrDefault(row => row.Level == level);
        var gold = rules.GoldReferences.SingleOrDefault(row => row.Level == level);
        if (reference.Level == 0 || gold.Level == 0 || reference.Value0 < 0 || reference.Value1 < 0)
            throw new InvalidDataException("当前等级的物品或金币引用不完整: " + level);
        decimal multiplier = DifficultyRates[difficulty];
        int Rate(int channel) => Math.Clamp(checked((int)Math.Min(DungeonDropRatePlan.ProbabilityDenominator,
            Math.Floor(probability.Values[channel] * rules.MonsterTypeBonus[channel * 4 + monsterType].ReferenceDouble * (double)multiplier))),
            0, DungeonDropRatePlan.ProbabilityDenominator);
        int minimum = level - reference.Value0, maximum = level + reference.Value1 - 1;
        ImmutableArray<DungeonDropRarityPool> Pools(bool equipment) => Enumerable.Range(0, 7).Select(rarity =>
            new DungeonDropRarityPool(rules.RarityDecision[rarity], candidates.ReferencePool(equipment, minimum, maximum, rarity))).ToImmutableArray();
        var stackables = Pools(false);
        var equipment = Pools(true);
        var trials = new List<DungeonDropTrial>
        {
            new(Rate(0), null, 1, 1, gold, multiplier, ReferenceGold: true),
            new(Rate(1), null, 1, 1, RarityPools: stackables),
            new(Rate(2), null, 1, 1, RarityPools: equipment),
            new(Rate(3), null, 1, 1, RarityPools: stackables)
        };
        var declaration = resolveMonster(monsterId);
        if (declaration.Items is { Count: > 0 } rows)
        {
            var explicitItems = rows.Where(row => row.Parameter > 0).Select(row => new DungeonWeightedDropItem(resolveItem(row.ItemId), row.Parameter));
            var pool = new DungeonItemDropPool(explicitItems);
            if (pool.TotalWeight > 0)
                trials.Add(new(DungeonExplicitDropPolicy.TriggerThreshold, pool, 1, 1));
        }
        if (special?.IndependentByMonster.TryGetValue(monsterId, out var independent) == true)
        foreach (var row in independent)
        {
            int basis = dungeonLevel ?? level;
            if (row.EnemyType != 0 || row.ListFlag == 2 || row.Difficulty >= 0 && row.Difficulty != difficulty ||
                row.MinimumLevel > 0 && row.MaximumLevel > 0 && (basis < row.MinimumLevel || basis > row.MaximumLevel))
                continue;
            int attempts = row.Attempts[difficulty], threshold = Math.Min(row.Probabilities[difficulty], 1000000);
            if (attempts == 0 || threshold == 0) continue;
            var source = row.ListFlag == 1 ? row.Items : row.ItemId > 0 ? ImmutableArray.Create(new DungeonSpecialDropItem(row.ItemId, 1)) : [];
            var pool = SpecialPool(source);
            if (pool.TotalWeight > 0)
                trials.Add(new(threshold, pool, 1, 1, ProbabilityDenominator: 1000000, Attempts: attempts));
        }
        if (special?.WorldByLevel.TryGetValue(level, out var world) == true)
        {
            var pool = SpecialPool(world.Items);
            if (pool.TotalWeight > 0)
                trials.Add(new(Math.Min(pool.TotalWeight, 100000), pool, 1, 1, ProbabilityDenominator: 100000));
        }
        return new(Rule, trials, referenceQuality: true);

        DungeonItemDropPool SpecialPool(IEnumerable<DungeonSpecialDropItem> source) => new(source
            .Where(row => row.ItemId > 0 && row.Weight > 0).Select(row => new DungeonWeightedDropItem(resolveItem(row.ItemId), row.Weight)));
    }
}
