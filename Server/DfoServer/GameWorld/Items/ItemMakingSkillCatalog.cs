using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed class ItemMakingSkillCatalog
{
    static readonly string[] Types = ["weapon", "cloth", "leather", "light armor", "heavy armor", "plate", "amulet", "wrist", "ring", "support", "magic stone"];
    public bool Available { get; }
    public ImmutableArray<(int Points, int CharacterLevel)> Limits { get; }
    public int ExperienceBase { get; }
    public int ExperienceRange { get; }
    public int RecipeLevelGap { get; }
    readonly FrozenDictionary<int, ItemMakingUpgrade> upgrades;

    public ItemMakingSkillCatalog(PvfArchive archive) : this(archive.Contains("etc/itemmakingskill.etc")
        ? ItemMakingSkillFile.Parse(archive.ReadTokens("etc/itemmakingskill.etc"), archive.ResolveString) : null) { }

    internal ItemMakingSkillCatalog(ItemMakingSkillFile? rules)
    {
        if (rules is null) { Limits = []; upgrades = FrozenDictionary<int, ItemMakingUpgrade>.Empty; return; }
        if (rules.Limits.IsEmpty || rules.Limits[0] != new ItemMakingLimit(0, 1) || rules.Limits.Any(row => row.Points is < 0 or > 1000 || row.CharacterLevel is < 1 or > 255) ||
            rules.Limits.Zip(rules.Limits.Skip(1)).Any(pair => pair.First.Points >= pair.Second.Points || pair.First.CharacterLevel >= pair.Second.CharacterLevel) ||
            rules.ExperienceBase < 1 || rules.ExperienceRange < 1 || (long)rules.ExperienceBase + rules.ExperienceRange - 1 > 1000 || rules.RecipeLevelGap < 1 ||
            rules.Upgrades.Any(row => row.Level < 1 || row.CommonMinimum < 0 || row.CommonMaximum > 31 || row.CommonMaximum < row.CommonMinimum ||
                row.RareMinimum < 0 || row.RareMaximum > 31 || row.RareMaximum < row.RareMinimum || row.CommonWeight is < 0 or > 10000 || row.RareWeight is < 0 or > 10000) ||
            rules.Upgrades.Select(row => row.Level).Distinct().Count() != rules.Upgrades.Length)
            throw new InvalidDataException("制作熟练度阈值或强化表无效");
        Available = true;
        Limits = rules.Limits.Select(row => (row.Points, row.CharacterLevel)).ToImmutableArray();
        ExperienceBase = rules.ExperienceBase;
        ExperienceRange = rules.ExperienceRange;
        RecipeLevelGap = rules.RecipeLevelGap;
        upgrades = rules.Upgrades.ToFrozenDictionary(row => row.Level);
    }

    public static bool TryType(string name, out byte type)
    {
        int index = Array.IndexOf(Types, name.Replace('_', ' ').Trim('[', ']').ToLowerInvariant());
        type = unchecked((byte)index);
        return index >= 0;
    }

    public int Level(int points, byte characterLevel) => Limits.Count(row => points >= row.Points && characterLevel >= row.CharacterLevel);

    public byte Upgrade(int level, Func<long, long> random)
    {
        if (!upgrades.TryGetValue(level, out var row)) return 0;
        bool common = Draw(random, 10000) < row.CommonWeight;
        int minimum = common ? row.CommonMinimum : row.RareMinimum;
        int maximum = common ? row.CommonMaximum : row.RareMaximum;
        // df_game_r保存的随机区间以千分单位展开，保留其999边界。
        return checked((byte)(minimum + Draw(random, 1000L * (maximum - minimum) + 999) / 1000));
    }

    internal static long Draw(Func<long, long> random, long maximum)
    {
        long value = random(maximum);
        return value >= 0 && value < maximum ? value : throw new InvalidDataException("制作随机值超出区间");
    }
}
