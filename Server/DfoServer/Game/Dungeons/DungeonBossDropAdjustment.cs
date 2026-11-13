using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 独立掉落保持原互斥池；在既有史诗和装备品质加成之后，只扩大装备命中区间。
internal sealed record DungeonBossDropAdjustment(int Percent)
{
    public long[] Widths(DungeonDropTrial trial)
    {
        var pool = trial.Items!;
        long denominator = (long)trial.ProbabilityDenominator * pool.TotalWeight;
        long[] widths;
        if (trial.EquipmentRarityAdjustment is { } rarity)
            widths = rarity.Widths(trial);
        else
        {
            widths = new long[pool.Entries.Length + 1];
            if (trial.EpicAdjustment is { } epic)
                epic.Widths(trial).ToArray().CopyTo(widths, 0);
            else
            {
                int previous = 0;
                for (int i = 0; i < pool.Entries.Length; i++)
                {
                    widths[i] = (long)trial.Threshold * (pool.Entries[i].CumulativeWeight - previous);
                    previous = pool.Entries[i].CumulativeWeight;
                }
            }
            widths[^1] = denominator - widths.Sum();
        }
        long equipment = 0, nonEquipment = 0;
        for (int i = 0; i < pool.Entries.Length; i++)
        {
            if (pool.Entries[i].Template.Kind == ItemKind.Equipment) equipment += widths[i];
            else nonEquipment += widths[i];
        }
        if (equipment == 0 || Percent == 0) return widths;
        // Int128保留联合整数网格精度，同时避免大分母、权重或增量乘法溢出。
        long enhanced = (long)Int128.Min(denominator - nonEquipment, (Int128)equipment * (100L + Percent) / 100);
        long cumulative = 0, allocated = 0;
        for (int i = 0; i < pool.Entries.Length; i++)
        {
            if (pool.Entries[i].Template.Kind != ItemKind.Equipment) continue;
            cumulative += widths[i];
            long next = (long)((Int128)enhanced * cumulative / equipment);
            widths[i] = next - allocated;
            allocated = next;
        }
        widths[^1] = denominator - nonEquipment - enhanced;
        return widths;
    }

    public ItemTemplate? Select(DungeonDropTrial trial, Func<int, int> sample)
    {
        var pool = trial.Items!;
        long roll = (long)sample(trial.ProbabilityDenominator) * pool.TotalWeight + sample(pool.TotalWeight);
        var widths = Widths(trial);
        long cumulative = 0;
        for (int i = 0; i < pool.Entries.Length; i++)
        {
            cumulative += widths[i];
            if (roll < cumulative) return pool.Entries[i].Template;
        }
        return null;
    }
}
