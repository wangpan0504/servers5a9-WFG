using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal readonly record struct EnhancementSuccessBonus(int Weight, long UntilMilliseconds)
{
    public long? QualificationExpiresAtMilliseconds => Weight > 0 ? UntilMilliseconds : null;

    public static EnhancementSuccessBonus Max(EnhancementSuccessBonus left, EnhancementSuccessBonus right) =>
        left.Weight > right.Weight ? left : right.Weight > left.Weight ? right :
        left.Weight > 0 ? new(left.Weight, Math.Max(left.UntilMilliseconds, right.UntilMilliseconds)) : default;
}

internal static class EnhancementPotionPolicy
{
    public const int ProbabilityDenominator = 100000;

    public static int ActiveBonusWeight(IEnumerable<ConsumableState> states,
        IReadOnlyDictionary<int, EnhancementPotionDefinition> definitions, EnhancementPotionMode mode, long now) =>
        ActiveBonus(states, definitions, mode, now).Weight;

    public static EnhancementSuccessBonus ActiveBonus(IEnumerable<ConsumableState> states,
        IReadOnlyDictionary<int, EnhancementPotionDefinition> definitions, EnhancementPotionMode mode, long now)
    {
        if (mode is not (EnhancementPotionMode.Reinforce or EnhancementPotionMode.Amplify))
            throw new ArgumentOutOfRangeException(nameof(mode));
        EnhancementSuccessBonus bonus = default;
        foreach (var state in states)
            if (state.Kind == ConsumableStateKind.Effect && state.Active(now) &&
                definitions.TryGetValue(state.ItemId, out var definition) && definition.Mode == mode)
            {
                if (definition.SuccessBonusWeight <= 0) throw new InvalidDataException("强化药剂加成必须为正数");
                bonus = EnhancementSuccessBonus.Max(bonus, new(definition.SuccessBonusWeight, state.UntilMilliseconds));
            }
        return bonus;
    }

    // 沿用便携增幅器的相对成功权重策略；只扩大成功区间，不增加随机次数或改变失败惩罚。
    public static int AdjustedSuccessWeight(int successWeight, int successBonusWeight)
    {
        if (successWeight is < 0 or > ProbabilityDenominator || successBonusWeight < 0)
            throw new ArgumentOutOfRangeException(successWeight is < 0 or > ProbabilityDenominator
                ? nameof(successWeight) : nameof(successBonusWeight));
        return (int)Math.Min(ProbabilityDenominator,
            successWeight * (ProbabilityDenominator + (long)successBonusWeight) / ProbabilityDenominator);
    }
}
