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
        var levels = dungeon.RawSections.Where(section => section.Tag == "recommended level").ToArray();
        if (levels.Length != 0)
            return levels is [{ Tokens: [{ Type: 0, Value: > 0 } minimum, { Type: 0 } maximum] }] &&
                maximum.Value >= minimum.Value && characterLevel >= minimum.Value && characterLevel <= maximum.Value;
        return dungeon.MinimumLevel is > 0 && dungeon.BasisLevel >= dungeon.MinimumLevel &&
            characterLevel >= dungeon.MinimumLevel && characterLevel <= dungeon.BasisLevel;
    }
}
