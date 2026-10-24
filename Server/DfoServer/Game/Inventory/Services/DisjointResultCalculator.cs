using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class DisjointResultCalculator
{
    public static InventoryReward[] Calculate(DisjointCatalog config, ItemTemplate template, Random random,
        InventoryReward[]? machineRewards = null)
    {
        var rewards = new Dictionary<int, int>();
        int rarity = template.Rarity;
        if (rarity is < 0 or > 6)
            return [];
        if (config.EventResults.Contains(template.Id))
            return [];
        if (config.SpecialResults.Contains(template.Id) && !config.IndependentResults.ContainsKey(template.Id) &&
            !config.PieceResults.ContainsKey(template.Id))
            return [];
        if (config.IndependentResults.TryGetValue(template.Id, out var independent) &&
            (independent.Length == 0 || independent.Any(row => row.ItemId <= 0 || row.Probability != 1000 || row.Count <= 0)))
            return [];
        if (machineRewards is { Length: 0 }) return [];
        if (machineRewards is not null)
            foreach (var reward in machineRewards) Add(reward.ItemId, reward.Count);
        else
        {
            Add(config.CubeItemId, checked((int)Math.Max(1, Math.Floor(template.DisjointValue * config.CubeMultipliers[rarity] / config.CubeBase))));
            if (config.Additional[rarity] is { } extra)
            {
                int item = extra.Items.Length == 1 ? extra.Items[0] : extra.Items[random.Next(extra.Items.Length)];
                double divisor = Percent() < extra.GreatChance && extra.GreatDivisor > 0 ? extra.GreatDivisor : extra.NormalDivisor;
                Add(item, LevelCount(divisor));
            }
        }
        // 新版PVF的灵魂、独立产物和碎片是公共追加规则，不能因使用玩家机器而丢失。
        if (!config.ExcludedSouls.Contains(template.Id) && config.Souls[rarity] is { } soul && Percent() < soul.NormalChance)
        {
            int count = LevelCount(soul.LevelDivisor);
            if (Percent() < soul.GreatChance) count++;
            Add(soul.ItemId, count);
        }
        if (independent is not null)
            foreach (var row in independent)
                Add(row.ItemId, row.Count);
        // 本服明确政策：整数均匀90..110，NPC及便携均不另乘机器倍率；不是已恢复的原服分布。
        if (config.PieceResults.TryGetValue(template.Id, out int piece))
            Add(piece, 90 + random.Next(21));
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
