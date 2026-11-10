using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 参考端10000分母及矩阵乘积规则的纯计算；列含义与候选绑定仍由正式掉落策略提供。
// 保留全部五列，不能把已读取的第五列静默丢弃或虚构为另一类别。
internal sealed record DungeonDropRatePlan(int MonsterLevel, byte MonsterType, byte Difficulty, byte PartyMembers,
    ImmutableArray<int> Thresholds, DungeonDropItemReference ItemReference, DungeonDropGoldReference GoldReference)
{
    public const int ProbabilityDenominator = 10000;
    public const int RarityDenominator = DungeonRarityThresholds.Denominator;

    public static DungeonDropRatePlan Prepare(DungeonDropRuleCatalog rules, int monsterLevel,
        byte monsterType, byte difficulty, byte partyMembers, int? dungeonLevel = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (monsterType > 3 || difficulty > 4 || partyMembers is < 1 or > 4)
            throw new InvalidDataException("掉落矩阵索引超出已声明的类型、难度或队伍范围");
        int probabilityLevel = dungeonLevel ?? monsterLevel;
        var probability = rules.ProbabilityRows.SingleOrDefault(row =>
            probabilityLevel >= row.MinimumLevel && probabilityLevel <= row.MaximumLevel)
            ?? throw new NotSupportedException("当前副本等级没有完整掉落概率段");
        var items = rules.ItemReferences.Where(row => row.Level == monsterLevel).ToArray();
        var gold = rules.GoldReferences.Where(row => row.Level == monsterLevel).ToArray();
        if (items.Length != 1 || gold.Length != 1)
            throw new NotSupportedException("当前生成等级缺少明确物品或金币引用记录");
        var thresholds = ImmutableArray.CreateBuilder<int>(5);
        for (int channel = 0; channel < 5; channel++)
        {
            decimal value = probability.Values[channel] * rules.MonsterTypeBonus[channel * 4 + monsterType].ReferenceDecimal *
                rules.DifficultyBonus[channel * 5 + difficulty].ReferenceDecimal * rules.PartyBonus[channel * 4 + partyMembers - 1].ReferenceDecimal;
            thresholds.Add(value >= ProbabilityDenominator ? ProbabilityDenominator : checked((int)decimal.Floor(value)));
        }
        return new(monsterLevel, monsterType, difficulty, partyMembers, thresholds.MoveToImmutable(), items[0], gold[0]);
    }

    public bool Hit(int channel, int roll)
    {
        if (channel is < 0 or >= 5 || roll is < 0 or >= ProbabilityDenominator)
            throw new ArgumentOutOfRangeException(nameof(roll));
        return roll < Thresholds[channel];
    }

    public int GoldAmount(int varianceRoll, decimal multiplier)
    {
        int variance = GoldReference.VariancePercent;
        if (varianceRoll < 0 || varianceRoll >= 2 * variance + 1 || multiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(varianceRoll));
        // 沿参考端整数波动取整，再按显式业务倍率下取整；不在这里推定金币数量矩阵含义。
        long change = (long)(varianceRoll - variance) * GoldReference.BaseAmount / 100;
        return checked((int)decimal.Floor((GoldReference.BaseAmount + change) * multiplier));
    }

    public static int Rarity(DungeonDropRuleCatalog rules, int row, int roll)
    {
        if (row is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(roll));
        return Rarity(rules.RarityDecision.AsSpan(row * 7, 7), roll);
    }

    public static int Rarity(ReadOnlySpan<int> thresholds, int roll)
    {
        if (thresholds.Length != 7 || roll is < 0 or >= RarityDenominator)
            throw new ArgumentOutOfRangeException(nameof(roll));
        for (int rarity = 0; rarity < 7; rarity++)
            if (roll + 1 <= thresholds[rarity])
                return rarity;
        throw new InvalidDataException("掉落稀有度阈值没有覆盖该原抽样，不能回退rarity0");
    }
}
