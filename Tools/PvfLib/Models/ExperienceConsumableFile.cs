using System.Collections.Immutable;

namespace PvfLib;

public sealed record ExperienceConsumableFile(StackableFile Stackable, StackableTypeDeclaration? Effect,
    int? CoolGroup, ImmutableArray<string> ExcludedJobs, ImmutableArray<string> DeclaredFields,
    ImmutableArray<string> Diagnostics)
{
    public static ExperienceConsumableFile Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var errors = document.Diagnostics.ToList();
        foreach (var duplicate in document.Sections.GroupBy(section => section.Name)
            .Where(group => group.Count() != 1 && !ItemUseFields.IsSharedTag(group.Key)))
            errors.Add("重复经验物品字段：" + duplicate.Key);
        var effects = document.Sections.Where(section => section.Name == "increase status type").ToArray();
        StackableTypeDeclaration? effect = null;
        if (effects is [{ Children.Length: 0, Values.Length: >= 2 }] && effects[0].Values[0].Type == 6 &&
            effects[0].Values.Skip(1).All(token => token.Type == 0) &&
            (effects[0].Values.Length == 2 || resolve(effects[0].Values[0].Value) == "[levelUpByTable]" &&
                (effects[0].Values.Length - 1) % 3 == 0))
            effect = new(resolve(effects[0].Values[0].Value), effects[0].Values.Skip(1).Select(token => token.Value).ToImmutableArray());
        else if (effects.Length != 0)
            errors.Add("经验增量声明不是单值或完整等级表");
        int? group = null;
        var groups = document.Sections.Where(section => section.Name == "cooltime group").ToArray();
        if (groups is [{ Children.Length: 0, Values.Length: 1 }] && groups[0].Values[0].Type == 0 && groups[0].Values[0].Value >= 0)
            group = groups[0].Values[0].Value;
        else if (groups.Length != 0)
            errors.Add("经验物品冷却组无效");
        var excluded = ImmutableArray<string>.Empty;
        var jobs = document.Sections.Where(section => section.Name == "impossible jobs").ToArray();
        if (jobs is [{ Children.Length: 0 }] && jobs[0].Values.All(token => token.Type == 6))
            excluded = jobs[0].Values.Select(token => resolve(token.Value)).ToImmutableArray();
        else if (jobs.Length != 0)
            errors.Add("经验物品排除职业声明无效");
        return new(StackableFile.Parse(id, path, tokens, resolve), effect, group, excluded,
            document.Sections.Select(section => section.Name).ToImmutableArray(), errors.ToImmutableArray());
    }
}
