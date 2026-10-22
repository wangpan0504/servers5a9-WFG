using DfoServer.Game.Currency;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal readonly record struct DungeonLuckyStarGrant(bool Granted, int Total);

// 幸运星资格只由当前角色等级与副本资源声明决定，不绑定角色、职业、账号或测试副本编号。
internal static class DungeonLuckyStarPolicy
{
    public static bool IsRecommendedLevel(DungeonDefinition dungeon, int characterLevel)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        if (characterLevel <= 0) return false;
        int minimum = dungeon.MinimumLevel.GetValueOrDefault();
        int basis = dungeon.BasisLevel.GetValueOrDefault();
        if (minimum <= 0 && basis <= 0)
            return false;
        if (minimum <= 0) minimum = basis;
        if (basis <= 0) basis = minimum;
        if (minimum > basis) (minimum, basis) = (basis, minimum);
        int characterMinimum = Math.Max(1, characterLevel - 5);
        int characterMaximum = checked(characterLevel + 5);
        return characterMaximum >= minimum && characterMinimum <= basis;
    }

    // 95 规则：地轨中心与哈林全图所有副本无条件计作推荐地下城；哈林突击、等级加速、地狱模式等独立模式副本不属于区域全图。
    private static readonly string[] RecommendedRegions = ["dungeon/metrocenter/", "dungeon/harlem/"];

    // 冒险团「推荐地下城通关」口径：团本排除；剧情/支线任务绑定入场无条件计入；
    // 地轨中心与哈林全图计入；其余按角色等级 ±5 与副本等级的区间相交。
    public static bool IsRecommendedClear(DungeonDefinition dungeon, uint questId, uint routeQuestId, int characterLevel)
    {
        ArgumentNullException.ThrowIfNull(dungeon);
        if (dungeon.IsRaid) return false;
        if (questId != 0 || routeQuestId != 0) return true;
        if (RecommendedRegions.Any(prefix => dungeon.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        return IsRecommendedLevel(dungeon, characterLevel);
    }

    public static DungeonLuckyStarGrant Apply(AccountWallet wallet, int stackLimit)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        if (stackLimit <= 0 || wallet.RentalPoints >= stackLimit)
            return new(false, wallet.RentalPoints);
        return new(true, checked(wallet.RentalPoints + 1));
    }
}
