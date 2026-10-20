using System.Collections.Immutable;

namespace PvfLib;

// 恢复四元组只保留原值，第三项不推断为治疗时间或倍率。
public sealed record RecoveryDeclaration(string Operation, int Amount, int Auxiliary, string Target);
public sealed record StatChangeDurationDeclaration(int Milliseconds, string Target, int? Parameter);

public sealed record RecoveryConsumableFile(int TemplateId, string ScriptPath, StackableFile Stackable,
    int? CoolTime, int? CoolGroup, RecoveryDeclaration? HpRecovery, RecoveryDeclaration? MpRecovery,
    ImmutableArray<string> DeclaredFields, ImmutableArray<string> Diagnostics)
{
    public int? SubType { get; init; }
    public int? ExpBonusPermille { get; init; }
    public int? ExpBonusMilliseconds { get; init; }
    public StatChangeDurationDeclaration? StatChangeDuration { get; init; }
    public byte? EffectMaintenance { get; init; }
    public byte? CooltimeMaintenance { get; init; }
    public ImmutableArray<int> LimitDungeons { get; init; } = [];

    public static RecoveryConsumableFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var errors = document.Diagnostics.ToList();
        if (document.Sections.Any(section => section.Children.Length != 0)) errors.Add("恢复物品含未确认的嵌套声明");
        foreach (var duplicate in document.Sections.GroupBy(section => section.Name).Where(group => group.Count() != 1))
            errors.Add("重复恢复物品字段：" + duplicate.Key);
        int? Scalar(string name)
        {
            var sections = document.Sections.Where(section => section.Name == name).ToArray();
            if (sections.Length == 0) return null;
            if (sections is [{ Children.Length: 0, Values.Length: 1 }] && sections[0].Values[0].Type == 0)
                return sections[0].Values[0].Value;
            errors.Add("恢复物品整数声明不完整：" + name);
            return null;
        }
        RecoveryDeclaration? Recovery(string name)
        {
            var sections = document.Sections.Where(section => section.Name == name).ToArray();
            if (sections.Length == 0) return null;
            if (sections is [{ Children.Length: 0, Values.Length: 4 }] && sections[0].Values[0].Type == 6 &&
                sections[0].Values[1].Type == 0 && sections[0].Values[2].Type == 0 && sections[0].Values[3].Type == 6)
            {
                var values = sections[0].Values;
                return new(resolve(values[0].Value), values[1].Value, values[2].Value, resolve(values[3].Value));
            }
            errors.Add("恢复物品四元组不完整：" + name);
            return null;
        }
        int? coolTime = Scalar("cool time"), group = Scalar("cooltime group"), subType = Scalar("sub type");
        var hp = Recovery("hp recovery"); var mp = Recovery("mp recovery");
        var duration = Duration(document, resolve, errors);
        byte? effects = Maintenance(document, "effect maintenance", 4, errors);
        byte? cooldowns = Maintenance(document, "cooltime maintenance", 3, errors);
        int? bonus = ExpBonus(document, resolve, errors);
        var limits = document.Sections.Where(section => section.Name == "limit dungeon")
            .SelectMany(section => section.Values).Where(token => token.Type == 0).Select(token => token.Value).ToImmutableArray();
        return new(id, path, StackableFile.Parse(id, path, tokens, resolve), coolTime, group, hp, mp,
            document.Sections.Select(section => section.Name).ToImmutableArray(), errors.ToImmutableArray())
        { SubType = subType, ExpBonusPermille = bonus,
            ExpBonusMilliseconds = duration is { Target: "myself" } ? duration.Milliseconds : null,
            StatChangeDuration = duration, EffectMaintenance = effects, CooltimeMaintenance = cooldowns, LimitDungeons = limits };
    }

    static int? ExpBonus(PvfTokenDocument document, Func<int, string> resolve, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == "exp bonus rate").ToArray();
        if (sections.Length == 0) return null;
        if (sections is [{ Children.Length: 0, Values: [{ } token] }])
        {
            float rate = token.Type == 2 ? BitConverter.Int32BitsToSingle(token.Value) : token.Type == 0 ? token.Value : float.NaN;
            if (float.IsFinite(rate) && rate > 0 && rate <= 10)
                return checked((int)Math.Round(rate * 1000, MidpointRounding.AwayFromZero));
        }
        errors.Add("经验加成倍率不完整");
        return null;
    }

    static StatChangeDurationDeclaration? Duration(PvfTokenDocument document, Func<int, string> resolve, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == "stat change duration").ToArray();
        if (sections.Length == 0) return null;
        if (sections is [{ Children.Length: 0 } section] && section.Values.Length is 2 or 3 &&
            section.Values[0] is { Type: 0, Value: > 0 } duration && section.Values[1] is { Type: 6 } target &&
            (section.Values.Length == 2 || section.Values[2].Type == 0))
            return new(duration.Value, resolve(target.Value), section.Values.Length == 3 ? section.Values[2].Value : null);
        errors.Add("持续效果时间声明不完整");
        return null;
    }

    static byte? Maintenance(PvfTokenDocument document, string name, int count, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == name).ToArray();
        if (sections.Length == 0) return null;
        if (sections is [{ Children.Length: 0 } section] && section.Values.Length == count &&
            section.Values.All(token => token.Type == 0 && token.Value is 0 or 1))
        {
            byte mask = 0;
            for (int i = 0; i < count; i++) mask |= checked((byte)(section.Values[i].Value << i));
            return mask;
        }
        errors.Add("持续效果保留声明不完整：" + name);
        return null;
    }
}
