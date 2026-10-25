using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed class SwitchingSupportSkillCatalog(
    ImmutableDictionary<(byte Job, byte Grow), ImmutableHashSet<ushort>> profiles)
{
    const string Path = "skill/switchingsupportskill.etc";
    static readonly string[] JobNames =
    [
        "swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer",
        "at priest", "gun blader"
    ];

    public static SwitchingSupportSkillCatalog Load(PvfArchive pvf)
    {
        PvfToken[] tokens = pvf.ReadTokens(Path);
        if (tokens.Length == 0)
            throw new InvalidDataException("Buff强化技能白名单资源为空: " + Path);
        return Parse(new PvfTokenDocument(tokens, pvf.ResolveString).Sections, pvf.ResolveString);
    }

    internal static SwitchingSupportSkillCatalog Parse(
        ImmutableArray<PvfTokenSection> sections,
        Func<int, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        var jobs = JobNames.Select((name, index) => (Name: Normalize(name), Job: checked((byte)index)))
            .ToDictionary(row => row.Name, row => row.Job, StringComparer.Ordinal);
        var result = ImmutableDictionary.CreateBuilder<(byte Job, byte Grow), ImmutableHashSet<ushort>>();
        (byte Job, byte Grow)? pending = null;
        foreach (var section in sections)
        {
            if (section.Name == "character type")
            {
                string[] labels = section.Values.Where(token => token.Type is 6 or 7)
                    .Select(token => Normalize(resolve(token.Value))).Where(value => value.Length != 0).ToArray();
                int[] integers = section.Values.Where(token => token.Type == 0).Select(token => token.Value).ToArray();
                if (labels.Length == 0 || integers.Length == 0 || !jobs.TryGetValue(labels[0], out byte job) ||
                    integers[0] is < 0 or >= 6)
                    throw new InvalidDataException("Buff强化职业白名单声明无效");
                pending = (job, checked((byte)integers[0]));
                continue;
            }
            if (section.Name != "skill" || pending is not { } profile)
                continue;
            if (section.Values.Length == 0 || section.Values.Length % 2 != 0 ||
                section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("Buff强化技能声明必须为技能ID与链接编号成对整数");
            var skillIds = ImmutableHashSet.CreateBuilder<ushort>();
            for (int index = 0; index < section.Values.Length; index += 2)
            {
                int skillId = section.Values[index].Value;
                if (skillId is <= 0 or >= ushort.MaxValue)
                    throw new InvalidDataException("Buff强化候选技能ID无效");
                // 第二列是客户端帮助链接编号，不是技能ID。
                skillIds.Add(checked((ushort)skillId));
            }
            var skills = skillIds.ToImmutable();
            if (skills.Count == 0 || result.ContainsKey(profile))
                throw new InvalidDataException("Buff强化技能白名单为空或职业配置重复");
            result.Add(profile, skills);
            pending = null;
        }
        if (result.Count == 0)
            throw new InvalidDataException("Buff强化技能白名单没有有效职业配置");
        return new(result.ToImmutable());
    }

    public IReadOnlySet<ushort> Skills(byte job, byte growType) =>
        profiles.GetValueOrDefault((job, (byte)(growType & 15))) ?? ImmutableHashSet<ushort>.Empty;

    public bool Supports(byte job, byte growType, ushort skillId) =>
        skillId != 0 && Skills(job, growType).Contains(skillId);

    static string Normalize(string value) => value.Replace("`", "", StringComparison.Ordinal)
        .Trim().Trim('[', ']').Trim().ToLowerInvariant();
}
