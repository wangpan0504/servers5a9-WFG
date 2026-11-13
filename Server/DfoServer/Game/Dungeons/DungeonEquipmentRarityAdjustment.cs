using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonEquipmentRarityAdjustment(EquipmentRarityBonus Bonus)
{
    public long[] Widths(DungeonDropTrial trial)
    {
        var pool = trial.Items!;
        long[] widths = new long[pool.Entries.Length + 1];
        int[] rarities = new int[widths.Length];
        var existing = trial.EpicAdjustment?.Widths(trial).ToArray();
        int previous = 0;
        long sum = 0;
        for (int i = 0; i < pool.Entries.Length; i++)
        {
            var row = pool.Entries[i];
            widths[i] = existing is not null ? existing[i] : (long)trial.Threshold * (row.CumulativeWeight - previous);
            previous = row.CumulativeWeight;
            sum = checked(sum + widths[i]);
            rarities[i] = row.Template.Kind == ItemKind.Equipment ? row.Template.Rarity : -1;
        }
        widths[^1] = checked((long)trial.ProbabilityDenominator * pool.TotalWeight - sum);
        rarities[^1] = -2;
        EquipmentRarityDistribution.Expand(widths, rarities, Bonus);
        return widths;
    }

    public ItemTemplate? Select(DungeonDropTrial trial, Func<int, int> sample)
    {
        var pool = trial.Items!;
        // 与既有定向史诗混池相同的联合整数网格，仍只选原试验的一项，不追加奖励机会。
        long roll = (long)sample(trial.ProbabilityDenominator) * pool.TotalWeight + sample(pool.TotalWeight);
        long cumulative = 0;
        var widths = Widths(trial);
        for (int i = 0; i < pool.Entries.Length; i++)
        {
            cumulative += widths[i];
            if (roll < cumulative) return pool.Entries[i].Template;
        }
        return null;
    }
}
