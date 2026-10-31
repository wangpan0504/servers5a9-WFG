using DfoServer.GameWorld.Dungeons;

namespace DfoServer.GameWorld.Guilds;

// 普通推荐地下城的公共积分资格在入场时按真实DGN冻结，不沿用冒险团的区域全图口径。
internal static class GuildDungeonMileageEligibility
{
    static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "guild dungeon", "raid dungeon", "raid normal dungeon", "normal luke dungeon", "anton dungeon",
        "tournament dungeon", "tower of despair", "tower of grief", "death tower map indexes",
        "defense dungeon", "blood dungeon", "dimension dungeon", "powerwar dungeon", "risk dungeon",
        "ancient dungeon", "event dungeon", "crack of dimension dungeon", "special dungeon", "kronos dungeon",
        "sao dungeon", "inferno dungeon", "ignore default dungeon clear", "advance altar type", "lost land parameters"
    };

    public static bool IsEligible(DungeonDefinition dungeon, int characterLevel)
    {
        if (characterLevel <= 0 || dungeon.IsTutorial || dungeon.IsClassExperience || dungeon.Id == 5000 ||
            dungeon.RawSections.Any(section => Excluded.Contains(section.Tag)) ||
            dungeon.DungeonType?.Trim().ToLowerInvariant() is { Length: > 0 } kind && kind is not ("[normal]" or "dungeon"))
            return false;
        return TryGetRecommendedLevels(dungeon, out int minimum, out int maximum) &&
            characterLevel >= minimum && characterLevel <= maximum;
    }

    // 在严格推荐区间中查找一个满足下限的等级；调用原资格保留特殊副本排除。
    public static bool HasEligibleLevelAtOrAbove(DungeonDefinition dungeon, int minimumLevel) =>
        TryGetRecommendedLevels(dungeon, out int minimum, out int maximum) &&
        Math.Max(minimumLevel, minimum) <= maximum && IsEligible(dungeon, Math.Max(minimumLevel, minimum));

    static bool TryGetRecommendedLevels(DungeonDefinition dungeon, out int minimum, out int maximum)
    {
        minimum = maximum = 0;
        var levels = dungeon.RawSections.Where(section => section.Tag == "recommended level").ToArray();
        if (levels.Length != 0)
        {
            if (levels is not [{ Tokens: [{ Type: 0, Value: > 0 } lower, { Type: 0 } upper] }] || upper.Value < lower.Value)
                return false;
            minimum = lower.Value;
            maximum = upper.Value;
            return true;
        }
        if (dungeon.MinimumLevel is not > 0 || dungeon.BasisLevel is not { } basis || basis < dungeon.MinimumLevel)
            return false;
        minimum = dungeon.MinimumLevel.Value;
        maximum = basis;
        return true;
    }
}
