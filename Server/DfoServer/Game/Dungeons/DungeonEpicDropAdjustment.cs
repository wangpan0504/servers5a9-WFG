using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 混池仍只选一个结果；史诗占用原空抽空间，非史诗保留原类别的绝对概率。
internal sealed record DungeonEpicDropAdjustment(decimal EpicThreshold)
{
    public static bool IsEpic(ItemTemplate item) => item is { Kind: ItemKind.Equipment, Rarity: 4 };

    public ItemTemplate? Select(DungeonDropTrial trial, Func<int, int> sample)
    {
        var pool = trial.Items!;
        // 两个原生整数骰构成精确联合网格，不把低概率压到万分之一，也不新增掉落次数。
        long roll = (long)sample(trial.ProbabilityDenominator) * pool.TotalWeight + sample(pool.TotalWeight);
        long cumulative = 0;
        int index = 0;
        foreach (long width in Widths(trial))
        {
            cumulative += width;
            if (roll < cumulative) return pool.Entries[index].Template;
            index++;
        }
        return null;
    }

    public IEnumerable<long> Widths(DungeonDropTrial trial)
    {
        var pool = trial.Items!;
        long denominator = (long)trial.ProbabilityDenominator * pool.TotalWeight;
        int epicWeight = 0, previous = 0;
        foreach (var row in pool.Entries)
        {
            int weight = row.CumulativeWeight - previous;
            previous = row.CumulativeWeight;
            if (IsEpic(row.Template)) epicWeight += weight;
        }
        long nonEpic = (long)trial.Threshold * (pool.TotalWeight - epicWeight);
        long available = denominator - nonEpic;
        long epic = EpicThreshold >= (decimal)available / epicWeight ? available :
            (long)decimal.Floor(EpicThreshold * epicWeight);
        int cumulativeEpicWeight = 0;
        long cumulativeEpic = 0;
        previous = 0;
        foreach (var row in pool.Entries)
        {
            int weight = row.CumulativeWeight - previous;
            previous = row.CumulativeWeight;
            if (!IsEpic(row.Template))
                yield return (long)trial.Threshold * weight;
            else
            {
                cumulativeEpicWeight += weight;
                long next = (long)decimal.Floor((decimal)epic * cumulativeEpicWeight / epicWeight);
                yield return next - cumulativeEpic;
                cumulativeEpic = next;
            }
        }
    }
}
