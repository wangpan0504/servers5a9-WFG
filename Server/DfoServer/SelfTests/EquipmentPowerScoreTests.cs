using System.Collections.Immutable;
using DfoServer.Game.Characters;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentPowerScoreTests
{
    internal static void RunCore()
    {
        int before = Count;
        var example = Example();
        var result = EquipmentPowerScore.Calculate(example);
        That(result.Status == EquipmentPowerStatus.Rated && result.Score == 6437 &&
            result.Breakdown is { Unrounded: 6436.716m, Base: 31000m, ElementFactor: 2m, CriticalFactor: 1.55m },
            "本服v0.1公开合成示例只在最后舍入为6437");
        That(result.AlgorithmVersion == "server-equipment-power/0.1-draft" && result.Context == example.Context &&
            result.Breakdown!.SkillFactors.SequenceEqual([1.1m]), "输出保留算法版本、来源上下文和独立技攻分项");
        var basic = example with { Element = EquipmentPowerElement.None, ElementStrength = null,
            RawMagicCritical = V("critical", 0), Yellow = V("yellow", 0), White = V("white", 0),
            CriticalDamage = V("burst", 0), FinalDamage = V("final", 0), SkillAttack = [] };
        That(EquipmentPowerScore.Calculate(basic) is { Score: 1000, Breakdown.ElementFactor: 1m },
            "明确无属性攻击时不套属强；已知零词条和空技攻列表合法");
        That(EquipmentPowerScore.Calculate(basic with { FinalMagicAttack = V("attack", 0) }) is { Status: EquipmentPowerStatus.Rated, Score: 0 },
            "真实零攻击可评零分，不与未知混同");
        foreach (var pair in new[] { (Attack: 4.9, Score: 0L), (Attack: 5.0, Score: 1L), (Attack: 15.0, Score: 2L) })
            That(EquipmentPowerScore.Calculate(basic with { FinalMagicAttack = V("attack", pair.Attack) }).Score == pair.Score,
                "非负展示分half-up边界");
        That(EquipmentPowerScore.Calculate(basic with { RawMagicCritical = V("critical", 1.10) }).Score == 1550 &&
            EquipmentPowerScore.Calculate(basic with { RawMagicCritical = V("critical", 1.0) }).Score == 1500,
            "原始暴击溢出继续贡献展示分而不被当概率封顶");
        That(EquipmentPowerScore.Calculate(basic with { CriticalDamage = V("burst", .3) }).Score == 1300,
            "本服设计零暴击时外层暴伤仍加分，不冒充实际期望伤害");
        var independent = basic with { SkillAttack = [V("skill1", .1), V("skill2", .2)] };
        That(EquipmentPowerScore.Calculate(independent).Score == 1320 &&
            EquipmentPowerScore.Calculate(independent with { SkillAttack = independent.SkillAttack.Reverse().ToImmutableArray() }).Score == 1320,
            "独立技攻各乘不加算，普通例子交换顺序不变");
        var precise = basic with { FinalMagicAttack = V("attack", 987654321.12345), SkillAttack =
            [V("z", .123456789012345), V("a", .987654321098765), V("m", .111111111111111), V("b", .333333333333333)] };
        var ordered = EquipmentPowerScore.Calculate(precise);
        var reversed = EquipmentPowerScore.Calculate(precise with { SkillAttack = precise.SkillAttack.Reverse().ToImmutableArray() });
        That(ordered.Score == reversed.Score && ordered.Breakdown!.Unrounded == reversed.Breakdown!.Unrounded &&
            ordered.Breakdown.SkillFactors.SequenceEqual(reversed.Breakdown.SkillFactors) &&
            ordered.Breakdown.SkillFactors[0] == 1.987654321098765m,
            "高精度多技攻按Ordinal组名固定乘法和分项顺序，不受输入排列影响");
        Expect(null, EquipmentPowerStatus.NotRated, "missing_input");
        Expect(example with { Context = null }, EquipmentPowerStatus.NotRated, "missing_context");
        Expect(example with { DamageKind = EquipmentPowerDamageKind.Unknown }, EquipmentPowerStatus.NotRated, "unsupported_damage_kind");
        Expect(example with { Element = EquipmentPowerElement.Unknown }, EquipmentPowerStatus.NotRated, "unknown_attack_element");
        foreach (var missing in new[] { example with { FinalMagicAttack = null }, example with { RawMagicCritical = null },
            example with { Yellow = null }, example with { White = null }, example with { CriticalDamage = null },
            example with { FinalDamage = null }, example with { ElementStrength = null }, example with { SkillAttack = default } })
            Expect(missing, EquipmentPowerStatus.NotRated, "missing_required_value");
        Expect(example with { UnsupportedEffects = ["unresolved_set_bonus"] }, EquipmentPowerStatus.NotRated, "unsupported_effects");
        Expect(example with { UnsupportedEffects = default }, EquipmentPowerStatus.NotRated, "unsupported_effects");
        Expect(example with { Element = (EquipmentPowerElement)99 }, EquipmentPowerStatus.InvalidInput, "invalid_attack_element");
        Expect(example with { Element = EquipmentPowerElement.None }, EquipmentPowerStatus.InvalidInput, "unexpected_element_strength");
        Expect(example with { Context = example.Context! with { CharacterRevision = -1 } }, EquipmentPowerStatus.InvalidInput, "invalid_context");
        Expect(example with { Context = example.Context! with { EquipmentSnapshotId = "" } }, EquipmentPowerStatus.InvalidInput, "invalid_context");
        foreach (double number in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
        {
            Expect(example with { FinalMagicAttack = V("attack", number) }, EquipmentPowerStatus.InvalidInput, "invalid_number");
            Expect(example with { SkillAttack = [V("skill", number)] }, EquipmentPowerStatus.InvalidInput, "invalid_number");
        }
        Expect(example with { FinalMagicAttack = V("attack", double.MaxValue) }, EquipmentPowerStatus.InvalidInput, "number_overflow");
        Expect(example with { FinalMagicAttack = V("attack", double.Epsilon) }, EquipmentPowerStatus.InvalidInput, "number_underflow");
        Expect(example with { FinalMagicAttack = V("attack", 1e28), Yellow = V("yellow", 1e28) }, EquipmentPowerStatus.InvalidInput, "score_overflow");
        Expect(basic with { FinalMagicAttack = V("attack", 1e21) }, EquipmentPowerStatus.InvalidInput, "score_overflow");
        Expect(example with { Yellow = V("bad", .2) with { SourceId = " " } }, EquipmentPowerStatus.InvalidInput, "invalid_source");
        Expect(example with { SkillAttack = [null!] }, EquipmentPowerStatus.InvalidInput, "invalid_source");
        Expect(example with { Yellow = example.White }, EquipmentPowerStatus.InvalidInput, "duplicate_source_effect");
        Expect(example with { SkillAttack = [example.FinalDamage!] }, EquipmentPowerStatus.InvalidInput, "duplicate_source_effect");
        Expect(example with { SkillAttack = [V("one", .1), V("two", .2) with { StackGroup = "one" }] },
            EquipmentPowerStatus.InvalidInput, "duplicate_skill_group");
        Expect(example with { SkillAttack = Enumerable.Range(0, 65).Select(i => V("s" + i, .1)).ToImmutableArray() },
            EquipmentPowerStatus.InvalidInput, "too_many_skill_groups");
        That(EquipmentPowerScore.Calculate(basic with { SkillAttack =
            [V("same_item", .1) with { EffectId = "one", StackGroup = "one" }, V("same_item", .2) with { EffectId = "two", StackGroup = "two" }] }).Score == 1320,
            "同一物品不同效果合法，不按来源物品粗暴去重");
        var repeated = EquipmentPowerScore.Calculate(example);
        That(repeated.Score == result.Score && repeated.Breakdown!.Unrounded == result.Breakdown!.Unrounded &&
            example.FinalMagicAttack!.Value == 10000 && example.SkillAttack[0].Value == .1, "输入无修改且重复计算确定性");
        Console.WriteLine($"EquipmentPowerScoreTests: {Count - before} passed");
    }

    static EquipmentPowerValue V(string id, double value) => new(id, id, id, value);
    static EquipmentPowerInput Example() => new(new("synthetic-magic-percent", "static-no-party", "synthetic-resource-v1",
        "synthetic-resolver-v1", "synthetic-equipment-snapshot", 1), EquipmentPowerDamageKind.MagicPercentage,
        EquipmentPowerElement.GuaranteedSingle, V("attack", 10000), V("element", 220), V("critical", 1.10),
        V("yellow", .20), V("white", .10), V("burst", .30), V("final", .10), [V("skill", .10)]);
    static void Expect(EquipmentPowerInput? input, EquipmentPowerStatus status, string reason)
    {
        var result = EquipmentPowerScore.Calculate(input);
        That(result.Status == status && result.Reason == reason && result.Score is null && result.Breakdown is null,
            "未评或拒绝不伪装成0分: " + reason);
    }
}
