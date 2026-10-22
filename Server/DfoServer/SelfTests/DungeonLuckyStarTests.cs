using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using PvfLib;
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
        // 95 规则：剧情任务绑定入场无条件计入；地轨中心与哈林全图计入；团本排除；其余仍按等级±5。
        var story = Definition(9002, 1, 10);
        That(DungeonLuckyStarPolicy.IsRecommendedClear(story, 3146, 0, 95) &&
            DungeonLuckyStarPolicy.IsRecommendedClear(story, 0, 3146, 95) &&
            !DungeonLuckyStarPolicy.IsRecommendedClear(story, 0, 0, 95),
            "剧情或支线任务绑定入场无条件计作推荐通关，普通入场仍按等级");
        var metro = Definition(310, 1, 10, "dungeon/metrocenter/time_plaza/time_plaza.dgn");
        var harlem = Definition(291100268, 1, 10, "dungeon/harlem/bordertown/bordertown.dgn");
        That(DungeonLuckyStarPolicy.IsRecommendedClear(metro, 0, 0, 95) &&
            DungeonLuckyStarPolicy.IsRecommendedClear(harlem, 0, 0, 95),
            "地轨中心与哈林全图即使等级不匹配也计作推荐通关");
        var assault = Definition(7300, 1, 10, "dungeon/harlem_assault/bordertown/bordertown_assault.dgn");
        That(!DungeonLuckyStarPolicy.IsRecommendedClear(assault, 0, 0, 95),
            "哈林突击等独立模式副本不按哈林全图计入");
        var raid = Definition(9595, 90, 95, "dungeon/anton_ordinary/phase1/close_blacksmoke.dgn", raid: true);
        That(!DungeonLuckyStarPolicy.IsRecommendedClear(raid, 3146, 0, 95) &&
            !DungeonLuckyStarPolicy.IsRecommendedClear(raid, 0, 0, 95),
            "团本即使等级或任务命中也不计推荐通关");
        That(DungeonLuckyStarPolicy.IsRecommendedClear(Definition(9001, 40, 45), 0, 0, 45) &&
            !DungeonLuckyStarPolicy.IsRecommendedClear(Definition(9001, 40, 45), 0, 0, 60),
            "非剧情非区域的普通副本按等级±5判定推荐通关");
        var initialGrant = DungeonLuckyStarPolicy.Apply(AccountWallet.Empty, 999);
        That(initialGrant.Granted && initialGrant.Total == 1 &&
            !DungeonLuckyStarPolicy.Apply(AccountWallet.Empty.Set(CurrencyKind.RentalPoint, 999), 999).Granted,
            "幸运星奖励按账号上限原子封顶");
        Console.WriteLine($"PASS {Count - before} dungeon lucky-star policy assertions");
    }

    private static DungeonDefinition Definition(int id, int? minimum, int? basis, string? path = null, bool raid = false) =>
        new(id, path ?? $"dungeon/{id}.dgn", minimum, basis, [],
            raid ? [new DungeonRawSection("raid dungeon", [new PvfToken(0, 1)])] : []);
}
