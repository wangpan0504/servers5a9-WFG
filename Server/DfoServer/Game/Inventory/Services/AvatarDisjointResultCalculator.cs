using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class AvatarDisjointResultCalculator
{
    public static InventoryReward[] Calculate(AvatarDisjointGradeTable table, Random random)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(random);
        var counts = new Dictionary<int, int>();
        var order = new List<int>();
        foreach (var pool in table.Pools)
        {
            int totalWeight = checked(pool.Rewards.Sum(reward => reward.Weight));
            for (int pick = 0; pick < pool.PickCount; pick++)
            {
                int roll = random.Next(totalWeight);
                AvatarDisjointRewardDefinition? selected = null;
                foreach (var reward in pool.Rewards)
                {
                    if (roll < reward.Weight)
                    {
                        selected = reward;
                        break;
                    }
                    roll -= reward.Weight;
                }
                if (selected is null)
                    throw new InvalidDataException($"时装分解等级{table.Grade}的加权抽取失败");
                if (!counts.ContainsKey(selected.ItemId))
                {
                    counts.Add(selected.ItemId, 0);
                    order.Add(selected.ItemId);
                }
                counts[selected.ItemId] = checked(counts[selected.ItemId] + selected.Count);
            }
        }
        return order.Select(itemId => new InventoryReward(itemId, counts[itemId])).ToArray();
    }
}
