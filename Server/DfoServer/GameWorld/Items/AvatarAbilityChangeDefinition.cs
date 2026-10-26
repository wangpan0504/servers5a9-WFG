using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AvatarAbilityChangeDefinition(int Grade, bool IncludesLower)
{
    public ImmutableHashSet<int> AllowedTargets { get; init; } = [];
    public bool OnlyFinite { get; init; }

    public static AvatarAbilityChangeDefinition? Parse(PvfTokenDocument document, Func<int, string> resolve)
    {
        var sections = document.Sections;
        if (document.Diagnostics.Length != 0 ||
            sections.Where(section => section.Name == "stackable type").ToArray() is not
                [{ IsBlock: false, Children.Length: 0, Values: [{ Type: 6 } type, { Type: 0, Value: 0 }] }] ||
            resolve(type.Value) != "[etc]" ||
            sections.Where(section => section.Name == "avatar select ability change").ToArray() is not
                [{ IsBlock: false, Children.Length: 0, Values: [{ Type: 6 } job, { Type: 6 } part,
                    { Type: 0 } grade, { Type: 0 } mode] }] ||
            resolve(job.Value) != "[all]" || resolve(part.Value) != "[all]") return null;
        // 已核对稀有专用3/0、高级及普通2/1；职业、部位和其它模式尚不借名称推断。
        AvatarAbilityChangeDefinition? rule = (grade.Value, mode.Value) switch
        {
            (3, 0) => new(3, false), (2, 1) => new(2, true), _ => null
        };
        if (rule is null || sections.Any(section => section.Name is not ("name" or "explain" or "flavor text" or
            "grade" or "rarity" or "usable job" or "attach type" or "minimum level" or "maximum level" or
            "weight" or "icon" or "icon mark" or "stackable type" or "move wav" or "stack limit" or
            "expiration date" or "usable period" or "impossible contents" or "item category" or "need material" or
            "purchase limit" or "daily purchase limit" or "field image" or "mileage item version" or "monthly delete" or
            "avatar select ability change" or "usable avatar index" or "use only with period avatar"))) return null;
        var period = sections.Where(section => section.Name == "use only with period avatar").ToArray();
        if (period.Length != 0)
        {
            if (period is not [{ IsBlock: false, Children.Length: 0, Values.Length: 0 }]) return null;
            rule = rule with { OnlyFinite = true };
        }
        var limits = sections.Where(section => section.Name == "usable avatar index").ToArray();
        if (limits.Length != 0)
        {
            if (limits is not [{ IsBlock: true, Children.Length: 0, Values.Length: > 0 } limit] ||
                limit.Values.Any(value => value.Type != 0 || value.Value <= 0)) return null;
            var targets = limit.Values.Select(value => value.Value).ToImmutableHashSet();
            if (targets.Count != limit.Values.Length) return null;
            rule = rule with { AllowedTargets = targets };
        }
        return rule;
    }

    public bool Allows(ItemTemplate target) => target.Kind == ItemKind.Avatar && target.EquipmentSlot <= 11 &&
        target.Grade > 0 && (IncludesLower ? target.Grade <= Grade : target.Grade == Grade) &&
        (AllowedTargets.IsEmpty || AllowedTargets.Contains(target.Id));
}
