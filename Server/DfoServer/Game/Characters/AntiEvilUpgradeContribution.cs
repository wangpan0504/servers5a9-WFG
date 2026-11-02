using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Characters;

internal static class AntiEvilUpgradeContribution
{
    public static int TotalValue(IEnumerable<int> flatValues, IEnumerable<float> percentageValues)
    {
        int sum = 0;
        foreach (int value in flatValues) sum = checked(sum + value);
        float multiplier = 1f;
        // CNChangeStatus的enum73将百分比加到6C0；UI只在最后执行一次单精度乘法。
        foreach (float value in percentageValues) multiplier += value * 0.01f;
        float total = (float)sum * multiplier;
        return checked((int)total);
    }

    public static int ConsumableFlatValue(IEnumerable<ConsumableState> states, long now,
        Func<int, RecoveryConsumableDefinition?> definition)
    {
        int value = 0;
        foreach (var state in states)
        {
            if (state.Kind != ConsumableStateKind.Effect || !state.Active(now)) continue;
            var effect = definition(state.ItemId) ?? throw new InvalidDataException("持续物品效果资源缺失");
            foreach (var change in effect.StatChanges)
            {
                if (change.Stat != "antievil") continue;
                if (change.Operation != "+")
                    throw new InvalidDataException("抗魔持续物品的运算尚未绑定");
                value = checked(value + change.Amount);
            }
        }
        return value;
    }

    public static bool TryCalculateForEquipment(AntiEvilUpgradeRules rules, int rarity, int equipmentSlot,
        byte effectiveLevel, out int value)
    {
        value = 0;
        if (effectiveLevel == 0) return true;
        int typeIndex = equipmentSlot switch
        {
            12 => 0,
            >= 14 and <= 18 => 1,
            >= 19 and <= 21 => 2,
            22 or 23 => 3,
            25 => 5,
            _ => -1
        };
        // 0296AB18的分支索引不等于资源列：耳环分支实际取第六项，旗帜分支不写抗魔。
        return typeIndex < 0 || TryCalculate(rules, rarity, typeIndex, (byte)(effectiveLevel - 1), out value);
    }

    public static int ForgeValue(ImmutableArray<ImmutableArray<int>> rows, int rarity, int effectiveLevel) =>
        effectiveLevel <= 0 || effectiveLevel > rows.Length || (uint)rarity >= (uint)rows[effectiveLevel - 1].Length
            ? 0 : rows[effectiveLevel - 1][rarity];

    // 03E58080取升级与锻造的较大值；02953C20再与基础抗魔相加。
    public static int EquipmentValue(int baseValue, int upgradeValue, int forgeValue) =>
        checked(baseValue + Math.Max(upgradeValue, forgeValue));

    public static bool TryCalculate(AntiEvilUpgradeRules rules, int rarity, int typeIndex, byte nativeIndex,
        out int value)
    {
        value = 0;
        if ((uint)rarity >= (uint)rules.RarityWeights.Length ||
            (uint)typeIndex >= (uint)rules.TypeWeights.Length || rules.Divisor <= 0)
            return false;
        // 03FC36B3 的单精度运算顺序；nativeIndex 是资源行索引，不在此推断装备等级。
        float weightedRarity = rules.RarityWeights[rarity] * 4f;
        float contribution = rules.TypeWeights[typeIndex] * weightedRarity;
        contribution /= rules.Divisor;
        contribution *= nativeIndex + 1;
        if (!float.IsFinite(contribution) || contribution < 0f)
            return false;
        float rounded = contribution + 0.5f;
        if (rounded >= 2147483648f)
            return false;
        value = (int)rounded;
        return true;
    }
}
