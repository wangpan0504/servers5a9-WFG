using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class DisjointResultCalculator
{
    public static InventoryReward[] Calculate(DisjointCatalog config, ItemTemplate template, Random random)
    {
        var rewards = new Dictionary<int, int>();
        int rarity = template.Rarity;
        if (rarity is < 0 or > 6)
            return [];
        Add(config.CubeItemId, checked((int)Math.Max(1, Math.Floor(template.DisjointValue * config.CubeMultipliers[rarity] / config.CubeBase))));
        if (config.Additional[rarity] is { } extra)
        {
            int item = extra.Items.Length == 1 ? extra.Items[0] : extra.Items[random.Next(extra.Items.Length)];
            double divisor = Percent() < extra.GreatChance && extra.GreatDivisor > 0 ? extra.GreatDivisor : extra.NormalDivisor;
            Add(item, LevelCount(divisor));
        }
        if (!config.ExcludedSouls.Contains(template.Id) && config.Souls[rarity] is { } soul && Percent() < soul.NormalChance)
        {
            int count = LevelCount(soul.LevelDivisor);
            if (Percent() < soul.GreatChance)
                count++;
            Add(soul.ItemId, count);
        }
        return rewards.Select(pair => new InventoryReward(pair.Key, pair.Value)).ToArray();

        double Percent() => random.Next(1_000_000) / 10_000.0;
        int LevelCount(double divisor)
        {
            if (divisor <= 0)
                return 1;
            double value = Math.Max(1, (int)template.MinimumLevel) / divisor;
            int count = checked((int)Math.Floor(value));
            if (random.Next(1_000_000) / 1_000_000.0 < value - count)
                count++;
            return Math.Max(1, count);
        }
        void Add(int item, int count) => rewards[item] = checked(rewards.GetValueOrDefault(item) + count);
    }
}
