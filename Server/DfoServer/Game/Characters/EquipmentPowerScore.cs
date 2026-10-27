using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

internal enum EquipmentPowerDamageKind { Unknown, MagicPercentage }
internal enum EquipmentPowerElement { Unknown, None, GuaranteedSingle }
internal enum EquipmentPowerStatus { Rated, NotRated, InvalidInput }

// 输入已由调用方归一并排除重复效果；不是客户端上报或89B基础属性的直接映射。
internal sealed record EquipmentPowerValue(string SourceId, string EffectId, string StackGroup, double Value);
internal sealed record EquipmentPowerContext(string ProfileId, string ContextId, string ResourceVersion,
    string ResolverVersion, string EquipmentSnapshotId, long CharacterRevision);
internal sealed record EquipmentPowerInput(EquipmentPowerContext? Context, EquipmentPowerDamageKind DamageKind,
    EquipmentPowerElement Element, EquipmentPowerValue? FinalMagicAttack, EquipmentPowerValue? ElementStrength,
    EquipmentPowerValue? RawMagicCritical, EquipmentPowerValue? Yellow, EquipmentPowerValue? White,
    EquipmentPowerValue? CriticalDamage, EquipmentPowerValue? FinalDamage,
    ImmutableArray<EquipmentPowerValue> SkillAttack)
{
    public ImmutableArray<string> UnsupportedEffects { get; init; } = [];
}

internal sealed record EquipmentPowerBreakdown(decimal Attack, decimal ElementFactor, decimal CriticalFactor,
    decimal Base, decimal YellowFactor, decimal WhiteFactor, decimal CriticalDamageFactor,
    decimal FinalDamageFactor, ImmutableArray<decimal> SkillFactors, decimal Unrounded);
internal sealed record EquipmentPowerResult(EquipmentPowerStatus Status, string Reason,
    EquipmentPowerContext? Context, long? Score = null, EquipmentPowerBreakdown? Breakdown = null)
{
    public string AlgorithmVersion => EquipmentPowerScore.Version;
}

// 本服展示分纯规则；不表示原版95算法、概率或DPS，不用于资产、奖励、战斗或入场。
internal static class EquipmentPowerScore
{
    public const string Version = "server-equipment-power/0.1-draft";
    public const decimal Scale = 0.1m, ElementDivisor = 220m, CriticalWeight = 0.5m;
    public const int MaximumSkillGroups = 64;

    public static EquipmentPowerResult Calculate(EquipmentPowerInput? input)
    {
        EquipmentPowerResult Fail(EquipmentPowerStatus status, string reason) => new(status, reason, input?.Context);
        if (input is null) return Fail(EquipmentPowerStatus.NotRated, "missing_input");
        var context = input.Context;
        if (context is null) return Fail(EquipmentPowerStatus.NotRated, "missing_context");
        if (!Identity(context.ProfileId) || !Identity(context.ContextId) || !Identity(context.ResourceVersion) ||
            !Identity(context.ResolverVersion) || !Identity(context.EquipmentSnapshotId) || context.CharacterRevision < 0)
            return Fail(EquipmentPowerStatus.InvalidInput, "invalid_context");
        if (input.DamageKind != EquipmentPowerDamageKind.MagicPercentage)
            return Fail(EquipmentPowerStatus.NotRated, "unsupported_damage_kind");
        if (input.Element == EquipmentPowerElement.Unknown)
            return Fail(EquipmentPowerStatus.NotRated, "unknown_attack_element");
        if (!Enum.IsDefined(input.Element)) return Fail(EquipmentPowerStatus.InvalidInput, "invalid_attack_element");
        if (input.UnsupportedEffects.IsDefault || !input.UnsupportedEffects.IsEmpty)
            return Fail(EquipmentPowerStatus.NotRated, "unsupported_effects");
        if (input.FinalMagicAttack is null || input.RawMagicCritical is null || input.Yellow is null ||
            input.White is null || input.CriticalDamage is null || input.FinalDamage is null || input.SkillAttack.IsDefault ||
            input.Element == EquipmentPowerElement.GuaranteedSingle && input.ElementStrength is null)
            return Fail(EquipmentPowerStatus.NotRated, "missing_required_value");
        if (input.Element == EquipmentPowerElement.None && input.ElementStrength is not null)
            return Fail(EquipmentPowerStatus.InvalidInput, "unexpected_element_strength");
        if (input.SkillAttack.Length > MaximumSkillGroups)
            return Fail(EquipmentPowerStatus.InvalidInput, "too_many_skill_groups");

        var sources = new HashSet<(string Source, string Effect)>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        string? error = null;
        decimal Read(EquipmentPowerValue? value)
        {
            if (value is null || !Identity(value.SourceId) || !Identity(value.EffectId) || !Identity(value.StackGroup))
            { error ??= "invalid_source"; return 0; }
            if (!sources.Add((value.SourceId, value.EffectId))) error ??= "duplicate_source_effect";
            if (!double.IsFinite(value.Value) || value.Value < 0)
            { error ??= "invalid_number"; return 0; }
            try
            {
                decimal number = checked((decimal)value.Value);
                if (value.Value > 0 && number == 0) error ??= "number_underflow";
                return number;
            }
            catch (OverflowException) { error ??= "number_overflow"; return 0; }
        }

        decimal attack = Read(input.FinalMagicAttack), critical = Read(input.RawMagicCritical);
        decimal yellow = Read(input.Yellow), white = Read(input.White), burst = Read(input.CriticalDamage);
        // 最终伤害/所有攻击力的同义项必须在上游归一到此唯一槽，不能额外再乘一项。
        decimal final = Read(input.FinalDamage);
        decimal element = input.ElementStrength is null ? 0 : Read(input.ElementStrength);
        var skills = ImmutableArray.CreateBuilder<decimal>(input.SkillAttack.Length);
        // 固定组顺序，避免decimal有限精度使等价输入排列得到不同末次舍入。
        var orderedSkills = input.SkillAttack.OrderBy(effect => effect?.StackGroup, StringComparer.Ordinal);
        foreach (var effect in orderedSkills)
        {
            decimal value = Read(effect);
            if (effect is not null && !groups.Add(effect.StackGroup)) error ??= "duplicate_skill_group";
            skills.Add(value);
        }
        if (error is not null) return Fail(EquipmentPowerStatus.InvalidInput, error);
        try
        {
            decimal elementFactor = input.Element == EquipmentPowerElement.None ? 1 : checked(1 + element / ElementDivisor);
            decimal criticalFactor = checked(1 + CriticalWeight * critical);
            decimal basis = checked(attack * elementFactor * criticalFactor);
            decimal yellowFactor = checked(1 + yellow), whiteFactor = checked(1 + white);
            decimal burstFactor = checked(1 + burst), finalFactor = checked(1 + final);
            decimal total = checked(Scale * basis * yellowFactor * whiteFactor * burstFactor * finalFactor);
            for (int i = 0; i < skills.Count; i++)
            {
                skills[i] = checked(1 + skills[i]);
                total = checked(total * skills[i]);
            }
            // 全部合法输入非负，AwayFromZero即最终一次half-up；不封顶原始暴击。
            long score = checked((long)decimal.Round(total, 0, MidpointRounding.AwayFromZero));
            return new(EquipmentPowerStatus.Rated, "rated", context, score,
                new(attack, elementFactor, criticalFactor, basis, yellowFactor, whiteFactor, burstFactor,
                    finalFactor, skills.MoveToImmutable(), total));
        }
        catch (OverflowException) { return Fail(EquipmentPowerStatus.InvalidInput, "score_overflow"); }
    }

    private static bool Identity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value == value.Trim();
}
