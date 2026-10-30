using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 展示每次独立抽样的边际概率，不把多次抽样误写成至少一次命中的概率。
internal sealed record DungeonDropListingRow(int Channel, string Category, int ItemId, string Name, int Rarity,
    decimal Probability, int MinimumCount, int MaximumCount, int Attempts, int Weight, int TotalWeight);

internal static class DungeonDropListing
{
    public static IEnumerable<DungeonDropListingRow> Rows(DungeonDropGenerationPlan plan)
    {
        for (int channel = 0; channel < plan.Trials.Length; channel++)
        {
            var trial = plan.Trials[channel];
            decimal trigger = (decimal)trial.Threshold / trial.ProbabilityDenominator;
            if (trial.Gold is not null)
            {
                yield return new(channel + 1, trial.Category.ToString(), 0, "金币（金额按原规则浮动）", 0,
                    trigger, trial.MinimumCount, trial.MaximumCount, trial.Attempts, 1, 1);
                continue;
            }
            if (trial.Items is { } direct)
                foreach (var row in Pool(direct, 1)) yield return row;
            int previous = 0;
            foreach (var rarity in trial.RarityPools.IsDefault ? [] : trial.RarityPools)
            {
                decimal probability = (decimal)(Math.Min(rarity.Threshold, DungeonDropRatePlan.RarityDenominator) -
                    Math.Min(previous, DungeonDropRatePlan.RarityDenominator)) / DungeonDropRatePlan.RarityDenominator;
                previous = rarity.Threshold;
                foreach (var row in Pool(rarity.Items, probability * (1000 - rarity.AlternatePermille - rarity.SecondaryPermille) / 1000)) yield return row;
                if (rarity.AlternateItems is { } alternate)
                    foreach (var row in Pool(alternate, probability * rarity.AlternatePermille / 1000)) yield return row;
                if (rarity.SecondaryItems is { } secondary)
                    foreach (var row in Pool(secondary, probability * rarity.SecondaryPermille / 1000)) yield return row;
            }
            IEnumerable<DungeonDropListingRow> Pool(DungeonItemDropPool pool, decimal branch)
            {
                int prior = 0;
                foreach (var entry in pool.Entries)
                {
                    int weight = entry.CumulativeWeight - prior;
                    prior = entry.CumulativeWeight;
                    int? count = trial.ItemCounts?.GetValueOrDefault(entry.Template.Id);
                    yield return new(channel + 1, trial.Category.ToString(), entry.Template.Id, entry.Template.Name,
                        entry.Template.Rarity, trigger * branch * weight / pool.TotalWeight,
                        count ?? trial.MinimumCount, count ?? trial.MaximumCount, trial.Attempts, weight, pool.TotalWeight);
                }
            }
        }
    }
}
