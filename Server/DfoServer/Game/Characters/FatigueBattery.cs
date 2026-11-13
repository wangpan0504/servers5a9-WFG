using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Game.Characters;

internal static class FatigueBattery
{
    // 当前客户端42181说明只加杀怪经验；30不是充能公式的等级除数。
    public const int KillExperiencePercent = 30;

    public static CharacterFatigueState Recharge(CharacterFatigueState state, FatigueBatteryCatalog catalog,
        int level, int endLevelCharacters, int elapsedDays)
    {
        state.Validate();
        if (elapsedDays <= 0 || endLevelCharacters < 0) throw new ArgumentOutOfRangeException(nameof(elapsedDays));
        if (!catalog.Eligible(level)) return state;
        // 旧服ComputeFatigueBatteryCharging先做整数除法，再按满级角色权重截断。
        // 已知日戳之间仅中间完整未上线日补156；首个旧存档日戳不补造历史疲劳。
        long unused = Math.Max(0, CharacterFatigueState.OrdinaryMaximum - state.Used) +
            (long)CharacterFatigueState.OrdinaryMaximum * (elapsedDays - 1);
        long basis = unused * level / catalog.LevelConstant;
        long added = (long)Math.Min(catalog.ChargingLimit, basis * (1d + endLevelCharacters * (double)catalog.EndLevelWeight));
        int charging = (int)Math.Min(catalog.ChargingLimit, state.BatteryCharging + added);
        if (charging < catalog.ActivationMinimum) return state with { BatteryCharging = (ushort)charging };
        int buffs = (int)Math.Min(catalog.BuffLimit(level), state.BatteryBuff + (long)charging * catalog.BuffPerCharging / catalog.ChargingPerBuff);
        // 原激活函数返回整份充能扣除量，转换余数及超出可用上限部分不留到下次重复转换。
        return state with { BatteryCharging = 0, BatteryBuff = (ushort)buffs };
    }

    public static ExperienceBonuses Apply(ExperienceBonuses reward, uint basis, bool eligible)
    {
        if (!eligible) return reward;
        if (reward.Parts.Any(part => part.Source == ExperienceBonusSource.FatigueBuff))
            throw new InvalidDataException("疲劳蓄电池经验不能重复提供");
        uint bonus = checked((uint)((ulong)basis * KillExperiencePercent / 100));
        if (bonus == 0) return reward;
        var result = reward with { Parts = reward.Parts.Add(new(ExperienceBonusSource.FatigueBuff, bonus))
            .OrderBy(part => part.Source).ToImmutableArray() };
        _ = result.Total;
        return result;
    }
}
