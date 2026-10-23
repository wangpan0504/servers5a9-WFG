using System.Collections.Immutable;

namespace PvfLib;

public sealed record ItemMakingLimit(int Points, int CharacterLevel);
public sealed record ItemMakingUpgrade(int Level, int CommonMinimum, int CommonMaximum, int CommonWeight,
    int RareMinimum, int RareMaximum, int RareWeight);
public sealed record ItemMakingSkillFile(ImmutableArray<ItemMakingLimit> Limits, int ExperienceBase, int ExperienceRange,
    int RecipeLevelGap, ImmutableArray<ItemMakingUpgrade> Upgrades)
{
    public static ItemMakingSkillFile Parse(PvfToken[] tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("制作熟练度资源结构无效");
        int[] Values(string name, int width)
        {
            var fields = document.Sections.Where(field => field.Name == name).ToArray();
            if (fields is not [{ Children.Length: 0 } field] || field.Values.IsEmpty || field.Values.Length % width != 0 ||
                field.Values.Any(value => value.Type != 0)) throw new InvalidDataException("制作熟练度字段不完整：" + name);
            return field.Values.Select(value => value.Value).ToArray();
        }
        var limits = Values("itemmaking exp limit", 2);
        var experience = Values("itemmaking exp", 2);
        var gap = Values("itemmaking level limit", 1);
        var upgrades = Values("upgrade with compound", 7);
        if (experience.Length != 2 || gap.Length != 1) throw new InvalidDataException("制作熟练度标量重复");
        return new(Enumerable.Range(0, limits.Length / 2).Select(i => new ItemMakingLimit(limits[i * 2], limits[i * 2 + 1])).ToImmutableArray(),
            experience[0], experience[1], gap[0], Enumerable.Range(0, upgrades.Length / 7).Select(i =>
                new ItemMakingUpgrade(upgrades[i * 7], upgrades[i * 7 + 1], upgrades[i * 7 + 2], upgrades[i * 7 + 3],
                    upgrades[i * 7 + 4], upgrades[i * 7 + 5], upgrades[i * 7 + 6])).ToImmutableArray());
    }
}
