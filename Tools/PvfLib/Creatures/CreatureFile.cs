using System.Collections.Immutable;

namespace PvfLib;

public sealed record CreatureSelectiveGrowth
{
    public int? UsingExperienceItem { get; init; }
    public int? ItemA { get; init; }
    public int? ItemB { get; init; }
    public int? SpeciesA { get; init; }
    public int? SpeciesB { get; init; }
    public int? SelectLevel { get; init; }
    public int? GoalLevel { get; init; }
    public int? ExperienceItem { get; init; }
    public int? ExperienceSpecies { get; init; }
    public int? ExperienceValue { get; init; }
}

// 仅提取服务端使用的CRE原始声明，运动、AI、技能脚本等继续由客户端消费。
public sealed record CreatureFile
{
    public int? MaximumLevel { get; init; }
    public int? StartLevel { get; init; }
    public int? EvolutionLevel { get; init; }
    public int? EvolutionQuest { get; init; }
    public int? EvolutionSpecies { get; init; }
    public int? SkillRecoveryTime { get; init; }
    public int? OverSkillRecoveryTime { get; init; }
    public int? OverSkillLevel { get; init; }
    public int? ItemsPerLevel { get; init; }
    public ImmutableArray<int> EvolutionChoices { get; init; } = [];
    public ImmutableArray<string> ArtifactSlots { get; init; } = [];
    public CreatureSelectiveGrowth? SelectiveGrowth { get; init; }

    public static CreatureFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve, preserveExtraEndTags: true);
        var fields = document.Sections;
        var growth = One(fields, "selective grow creature");
        var children = growth?.Children ?? [];
        var slots = One(fields, "artifact slot");
        return new()
        {
            MaximumLevel = Scalar(fields, "max level"), StartLevel = Scalar(fields, "start level"),
            EvolutionLevel = Scalar(fields, "evolution level"), EvolutionQuest = Scalar(fields, "evolution quest"),
            EvolutionSpecies = Scalar(fields, "evolution creature id"),
            SkillRecoveryTime = Scalar(fields, "skill recovery time"), OverSkillRecoveryTime = Scalar(fields, "over skill recovery time"),
            OverSkillLevel = Scalar(fields, "learn overskill level"),
            ItemsPerLevel = Scalar(children, "item count for levelup") ?? Scalar(fields, "item count for levelup"),
            EvolutionChoices = Integers(fields, "evolution creature multy id"),
            ArtifactSlots = slots is null ? [] : slots.Values.Select(value => value.Type is 6 or 9 ? resolve(value.Value) :
                throw new InvalidDataException("CRE宠物装备槽不是字符串")).ToImmutableArray(),
            SelectiveGrowth = growth is null ? null : new()
            {
                UsingExperienceItem = Scalar(children, "using exp item"),
                ItemA = Scalar(children, "grow Atype item index"), ItemB = Scalar(children, "grow Btype item index"),
                SpeciesA = Scalar(children, "grow Atype creature id"), SpeciesB = Scalar(children, "grow Btype creature id"),
                SelectLevel = Scalar(children, "grow level"), GoalLevel = Scalar(children, "grow goal level"),
                ExperienceItem = Scalar(children, "grow EXPtype item index"), ExperienceSpecies = Scalar(children, "grow EXPtype creature id"),
                ExperienceValue = Scalar(children, "add EXPtype exp value")
            }
        };
    }

    static PvfTokenSection? One(ImmutableArray<PvfTokenSection> sections, string name)
    {
        var matches = sections.Where(section => section.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length <= 1 ? matches.FirstOrDefault() : throw new InvalidDataException("重复CRE声明: " + name);
    }

    static int? Scalar(ImmutableArray<PvfTokenSection> sections, string name)
    {
        var field = One(sections, name);
        if (field is null) return null;
        return field.Values is [{ Type: 0 } value] ? value.Value : throw new InvalidDataException("CRE字段不是单个整数: " + name);
    }

    static ImmutableArray<int> Integers(ImmutableArray<PvfTokenSection> sections, string name)
    {
        var field = One(sections, name);
        return field is null ? [] : field.Values.Select(value => value.Type == 0 ? value.Value :
            throw new InvalidDataException("CRE字段不是整数数组: " + name)).ToImmutableArray();
    }
}
