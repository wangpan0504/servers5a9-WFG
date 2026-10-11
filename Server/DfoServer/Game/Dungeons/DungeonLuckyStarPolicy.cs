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

    public static DungeonLuckyStarGrant Apply(AccountWallet wallet, int stackLimit)
    {
        ArgumentNullException.ThrowIfNull(wallet);
        if (stackLimit <= 0 || wallet.RentalPoints >= stackLimit)
            return new(false, wallet.RentalPoints);
        return new(true, checked(wallet.RentalPoints + 1));
    }
}
