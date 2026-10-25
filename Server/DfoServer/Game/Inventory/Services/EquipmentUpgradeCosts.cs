using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class EquipmentUpgradeCosts
{
    // 客户端先比较两种打造的累计倍率，选较贵者；每档使用单精度并截断，金币最后取整到万。
    internal static ItemMaterialCost[] Calculate(EquipmentUpgradeGroup group, bool fusion,
        int upgrade, int separate, int? cardRarity, IReadOnlyList<int> emblemRarities)
    {
        var costs = new Dictionary<int, int>();
        Add(fusion ? group.FusionMaterials : group.Materials);
        if (group.InheritAbility) return Result();

        bool useSeparate = Rate(10000, upgrade, group.UpgradeRates) <
            Rate(10000, separate, group.SeparateRates);
        foreach (var cost in group.AdditionMaterials)
        {
            int count = useSeparate ? Rate(cost.Count, separate, group.SeparateRates)
                : Rate(cost.Count, upgrade, group.UpgradeRates);
            if (cost.ItemId == 0) count = count / 10000 * 10000;
            Add([new(cost.ItemId, count)]);
        }
        if (cardRarity is { } card && group.CardMaterials.TryGetValue(card, out var cardCosts))
            Add(cardCosts);
        foreach (int rarity in emblemRarities)
            if (group.EmblemMaterials.TryGetValue(rarity, out var emblemCosts)) Add(emblemCosts);
        return Result();

        void Add(IEnumerable<ItemMaterialCost> values)
        {
            foreach (var value in values)
                costs[value.ItemId] = checked(costs.GetValueOrDefault(value.ItemId) + value.Count);
        }

        ItemMaterialCost[] Result() => costs.Where(pair => pair.Value != 0)
            .Select(pair => new ItemMaterialCost(pair.Key, pair.Value)).ToArray();
    }

    static int Rate(int count, int level, IReadOnlyDictionary<int, decimal> rates)
    {
        foreach (var pair in rates.OrderBy(pair => pair.Key))
        {
            if (pair.Key > level) break;
            count = checked((int)((float)count * (float)pair.Value));
        }
        return count;
    }
}
