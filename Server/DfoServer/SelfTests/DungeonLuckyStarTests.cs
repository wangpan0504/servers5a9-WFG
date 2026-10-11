using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonLuckyStarTests
{
    public static void RunCore()
    {
        int before = Count;
        var recommended = Definition(9001, 40, 45);
        That(DungeonLuckyStarPolicy.IsRecommendedLevel(recommended, 35),
            "推荐地图按角色等级区间命中下边界");
        That(DungeonLuckyStarPolicy.IsRecommendedLevel(recommended, 50),
            "推荐地图按角色等级区间命中上边界");
        That(!DungeonLuckyStarPolicy.IsRecommendedLevel(recommended, 34) &&
            !DungeonLuckyStarPolicy.IsRecommendedLevel(recommended, 51),
            "推荐地图区间外不发幸运星");
        That(DungeonLuckyStarPolicy.IsRecommendedLevel(Definition(7777, 1, 1), 1) &&
            DungeonLuckyStarPolicy.IsRecommendedLevel(Definition(123456, 90, 95), 95),
            "推荐判定不绑定测试副本或测试角色编号");
        That(DungeonLuckyStarPolicy.IsRecommendedLevel(Definition(3, null, 50), 50) &&
            DungeonLuckyStarPolicy.IsRecommendedLevel(Definition(4, 50, null), 50),
            "仅声明一侧等级的资源仍按有效推荐区间生效");
        That(!DungeonLuckyStarPolicy.IsRecommendedLevel(Definition(5, null, null), 50),
            "完全缺失等级声明安全拒绝奖励");
        var initialGrant = DungeonLuckyStarPolicy.Apply(AccountWallet.Empty, 999);
        That(initialGrant.Granted && initialGrant.Total == 1 &&
            !DungeonLuckyStarPolicy.Apply(AccountWallet.Empty.Set(CurrencyKind.RentalPoint, 999), 999).Granted,
            "幸运星奖励按账号上限原子封顶");
        Console.WriteLine($"PASS {Count - before} dungeon lucky-star policy assertions");
    }

    private static DungeonDefinition Definition(int id, int? minimum, int? basis) =>
        new(id, $"dungeon/{id}.dgn", minimum, basis, [], []);
}
