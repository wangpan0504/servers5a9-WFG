using System.Collections.Immutable;

namespace PvfLib;

// 恢复四元组只保留原值，第三项不推断为治疗时间或倍率。
public sealed record RecoveryDeclaration(string Operation, int Amount, int Auxiliary, string Target);
public sealed record StatChangeDurationDeclaration(int Milliseconds, string Target, int? Parameter);
public sealed record StatChangeDeclaration(string Operation, int Amount, string Stat);
public sealed record DisguiseDeclaration(string Kind, int FormId, int Seconds);

public sealed record RecoveryConsumableFile(int TemplateId, string ScriptPath, StackableFile Stackable,
    int? CoolTime, int? CoolGroup, RecoveryDeclaration? HpRecovery, RecoveryDeclaration? MpRecovery,
    ImmutableArray<string> DeclaredFields, ImmutableArray<string> Diagnostics)
{
    public int? SubType { get; init; }
    public int? TotalUsableCount { get; init; }
    public ImmutableArray<string> StructureDiagnostics { get; init; } = [];
    public int? ExpBonusPermille { get; init; }
    public int? QuestExpBonusPermille { get; init; }
    public int? ExpBonusMilliseconds { get; init; }
    public StatChangeDurationDeclaration? StatChangeDuration { get; init; }
    public ImmutableArray<StatChangeDeclaration> StatChanges { get; init; } = [];
    public byte? EffectMaintenance { get; init; }
    public byte? CooltimeMaintenance { get; init; }
    public ImmutableArray<int> LimitDungeons { get; init; } = [];
    public ImmutableArray<string> ImpossibleContents { get; init; } = [];
    public ImmutableArray<int>? ActionUsableDungeons { get; init; }
    public DisguiseDeclaration? Disguise { get; init; }
    public ImmutableArray<DisguiseDeclaration> RandomDisguises { get; init; } = [];
    public ImmutableArray<StatChangeDeclaration> DisguiseAppendages { get; init; } = [];
    public int? UnlimitedWaste { get; init; }

    public static RecoveryConsumableFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var structureErrors = document.Diagnostics.ToList();
        if (document.Sections.Any(section => section.Children.Length != 0)) structureErrors.Add("恢复物品含未确认的嵌套声明");
        var errors = new List<string>(structureErrors);
        var stackable = StackableFile.Parse(id, path, tokens, resolve);
        foreach (var duplicate in document.Sections.GroupBy(section => section.Name).Where(group => group.Count() != 1))
            if (!(duplicate.Key is "stackable type" && stackable.TypeDeclaration is not null ||
                ItemUseFields.IsSharedTag(duplicate.Key) ||
                // 图标标记属于显示元数据，可有多条完整声明，不构成使用字段冲突。
                duplicate.Key is "icon mark" && duplicate.All(section => section is
                    { Children.Length: 0, Values: [{ Type: 6 }, { Type: 0, Value: >= 0 }] }) ||
                duplicate.Key is "impossible contents" && duplicate.All(section => section.IsBlock &&
                    section.Children.IsEmpty && section.Values.All(token => token.Type == 6)) ||
                CompleteIntegerScalars(duplicate) && AllowsRepeatedScalar(duplicate.Key, duplicate)))
                errors.Add("重复恢复物品字段：" + duplicate.Key);
        int? Scalar(string name)
        {
            var sections = document.Sections.Where(section => section.Name == name).ToArray();
            if (sections.Length == 0) return null;
            // 原客户端按脚本顺序直接赋值冷却标量，后声明覆盖前声明；畸形声明不参与兼容。
            if (CompleteIntegerScalars(sections) && (sections.Length == 1 || AllowsRepeatedScalar(name, sections)))
                return sections[^1].Values[0].Value;
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
        int? coolTime = Scalar("cool time"), group = Scalar("cooltime group"), subType = Scalar("sub type"), totalUses = Scalar("total usable count");
        var hp = Recovery("hp recovery"); var mp = Recovery("mp recovery");
        var duration = Duration(document, resolve, errors);
        var statChanges = StatChangesFrom(document, resolve, errors);
        var disguise = stackable.TypeDeclaration?.Label == "[disguise]" ? DisguiseFrom(document, resolve, errors) : null;
        var randomDisguises = stackable.TypeDeclaration?.Label == "[random disguise]"
            ? RandomDisguisesFrom(document, resolve, errors) : [];
        var disguiseAppendages = DisguiseAppendagesFrom(document, resolve, errors);
        int? unlimitedWaste = Scalar("unlimited waste");
        byte? effects = Maintenance(document, "effect maintenance", 4, errors);
        byte? cooldowns = Maintenance(document, "cooltime maintenance", 3, errors);
        int? bonus = ExpBonus(document, "exp bonus rate", errors);
        int? questBonus = ExpBonus(document, "quest exp bonus rate", errors);
        var limitSections = document.Sections.Where(section => section.Name == "limit dungeon").ToArray();
        var limits = ImmutableArray<int>.Empty;
        if (limitSections.Length != 0)
        {
            if (limitSections is [{ Children.Length: 0 } section] &&
                section.Values.All(token => token.Type == 0 && token.Value > 0))
                limits = section.Values.Select(token => token.Value).ToImmutableArray();
            else { limits = default; errors.Add("恢复物品禁用地下城声明不完整"); }
        }
        var impossibleSections = document.Sections.Where(section => section.Name == "impossible contents").ToArray();
        var impossible = ImmutableArray<string>.Empty;
        if (impossibleSections.Length != 0)
        {
            if (impossibleSections.All(section => section.IsBlock && section.Children.IsEmpty &&
                section.Values.All(token => token.Type == 6)))
                impossible = impossibleSections.SelectMany(section => section.Values)
                    .Select(token => resolve(token.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            else { impossible = default; errors.Add("恢复物品禁止用途声明不完整"); }
        }
        var usableSections = document.Sections.Where(section => section.Name == "action usable dungeon").ToArray();
        ImmutableArray<int>? usable = null;
        if (usableSections.Length != 0)
        {
            if (usableSections is [{ Children.Length: 0, Values.Length: > 0 } section] &&
                section.Values.All(token => token.Type == 0 && token.Value > 0))
                usable = section.Values.Select(token => token.Value).ToImmutableArray();
            else errors.Add("恢复物品可用地下城声明不完整");
        }
        return new(id, path, stackable, coolTime, group, hp, mp,
            document.Sections.Select(section => section.Name).ToImmutableArray(), errors.ToImmutableArray())
        { SubType = subType, TotalUsableCount = totalUses, StructureDiagnostics = structureErrors.ToImmutableArray(),
            ExpBonusPermille = bonus, QuestExpBonusPermille = questBonus,
            ExpBonusMilliseconds = duration is { Target: "myself" } ? duration.Milliseconds : null,
            StatChangeDuration = duration, StatChanges = statChanges,
            EffectMaintenance = effects, CooltimeMaintenance = cooldowns, LimitDungeons = limits, ImpossibleContents = impossible,
            ActionUsableDungeons = usable, Disguise = disguise, RandomDisguises = randomDisguises,
            DisguiseAppendages = disguiseAppendages, UnlimitedWaste = unlimitedWaste };
    }

    static DisguiseDeclaration? DisguiseFrom(PvfTokenDocument document, Func<int, string> resolve, List<string> errors)
    {
        var kinds = document.Sections.Where(section => section.Name == "string data").ToArray();
        var parameters = document.Sections.Where(section => section.Name == "int data").ToArray();
        if (kinds.Length == 0 && parameters.Length == 0) return null;
        if (kinds is [{ Children.Length: 0, Values: [{ Type: 6 } kind] }] &&
            parameters is [{ Children.Length: 0, Values: [{ Type: 0, Value: > 0 } form, { Type: 0, Value: > 0 } seconds] }])
            return new(resolve(kind.Value), form.Value, seconds.Value);
        // 变身只保留完整的一组形态与时长，不从多选或随机向量挑一个默认值。
        errors.Add("变身形态及持续时间声明不完整");
        return null;
    }

    static ImmutableArray<DisguiseDeclaration> RandomDisguisesFrom(PvfTokenDocument document,
        Func<int, string> resolve, List<string> errors)
    {
        var kinds = document.Sections.Where(section => section.Name == "string data").ToArray();
        var parameters = document.Sections.Where(section => section.Name == "int data").ToArray();
        if (kinds is [{ Children.Length: 0, Values.Length: > 0 } strings] &&
            parameters is [{ Children.Length: 0 } integers] && integers.Values.Length == strings.Values.Length * 2 &&
            strings.Values.All(token => token.Type == 6) && integers.Values.All(token => token.Type == 0 && token.Value > 0))
        {
            var entries = ImmutableArray.CreateBuilder<DisguiseDeclaration>(strings.Values.Length);
            for (int i=0;i<strings.Values.Length;i++)
                entries.Add(new(resolve(strings.Values[i].Value),integers.Values[2*i].Value,integers.Values[2*i+1].Value));
            return entries.MoveToImmutable();
        }
        errors.Add("随机变身类型及形态时长向量不完整");
        return [];
    }

    static ImmutableArray<StatChangeDeclaration> DisguiseAppendagesFrom(PvfTokenDocument document,
        Func<int, string> resolve, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == "disguise appendage").ToArray();
        if (sections.Length == 0) return [];
        if (sections is [{ Children.Length: 0, Values.Length: > 0 } section] && section.Values.Length % 3 == 0)
        {
            var entries = ImmutableArray.CreateBuilder<StatChangeDeclaration>(section.Values.Length / 3);
            for (int i=0;i<section.Values.Length;i+=3)
            {
                if (section.Values[i].Type != 6 || section.Values[i+1].Type != 6 || section.Values[i+2].Type != 0) break;
                entries.Add(new(resolve(section.Values[i+1].Value),section.Values[i+2].Value,resolve(section.Values[i].Value)));
            }
            if (entries.Count == section.Values.Length / 3) return entries.MoveToImmutable();
        }
        errors.Add("变身附加属性三元组不完整");
        return [];
    }

    static bool CompleteIntegerScalars(IEnumerable<PvfTokenSection> sections) =>
        sections.All(section => section is { Children.Length: 0, Values: [{ Type: 0 }] });

    static ImmutableArray<StatChangeDeclaration> StatChangesFrom(PvfTokenDocument document,
        Func<int, string> resolve, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == "stat change").ToArray();
        if (sections.Length == 0) return [];
        if (sections is [{ Children.Length: 0 } section] && section.Values.Length > 0 && section.Values.Length % 3 == 0)
        {
            var entries = ImmutableArray.CreateBuilder<StatChangeDeclaration>(section.Values.Length / 3);
            for (int i = 0; i < section.Values.Length; i += 3)
            {
                if (section.Values[i].Type != 6 || section.Values[i + 1].Type != 0 || section.Values[i + 2].Type != 6)
                    break;
                entries.Add(new(resolve(section.Values[i].Value), section.Values[i + 1].Value, resolve(section.Values[i + 2].Value)));
            }
            if (entries.Count == section.Values.Length / 3) return entries.MoveToImmutable();
        }
        errors.Add("持续属性声明不完整");
        return [];
    }

    // 日次数及恢复目录的正堆叠上限仅兼容相同完整值，冲突或坏声明继续保留诊断。
    static bool AllowsRepeatedScalar(string name, IEnumerable<PvfTokenSection> sections) =>
        name is "cool time" or "cooltime group" ||
        (name == "total usable count" || name == "stack limit" && sections.All(section => section.Values[0].Value > 0)) &&
        sections.Select(section => section.Values[0].Value).Distinct().Count() == 1;

    static int? ExpBonus(PvfTokenDocument document, string field, List<string> errors)
    {
        var sections = document.Sections.Where(section => section.Name == field).ToArray();
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
