using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 特殊副本通关奖励与礼盒容器日/周上限判定（etc/clearrewardlimit.etc）。
internal static class ClearRewardLimitPolicy
{
    public static DungeonIncountPeriod Period(DateTimeOffset now) =>
        DungeonIncountPolicy.Period(now);

    public static int DailyRemaining(ClearRewardDungeonLimit limit, DungeonIncountUsage usage) =>
        Math.Max(0, checked(limit.DailyLimit + usage.DailyExtra) - usage.DailyUsed);

    public static int WeeklyRemaining(ClearRewardDungeonLimit limit, DungeonIncountUsage usage) =>
        Math.Max(0, checked(limit.WeeklyLimit + usage.WeeklyExtra) - usage.WeeklyUsed);

    public static bool CanGrantDungeonReward(ClearRewardDungeonLimit limit, DungeonIncountUsage usage) =>
        DailyRemaining(limit, usage) > 0 && WeeklyRemaining(limit, usage) > 0;

    public static int DailyItemRemaining(ClearRewardItemLimit limit, DungeonIncountUsage usage) =>
        Math.Max(0, checked(limit.DailyLimit + usage.DailyExtra) - usage.DailyUsed);

    public static int WeeklyItemRemaining(ClearRewardItemLimit limit, DungeonIncountUsage usage) =>
        Math.Max(0, checked(limit.WeeklyLimit + usage.WeeklyExtra) - usage.WeeklyUsed);

    public static bool CanGrantItemReward(ClearRewardItemLimit limit, DungeonIncountUsage usage) =>
        DailyItemRemaining(limit, usage) > 0 && WeeklyItemRemaining(limit, usage) > 0;

    public static bool AllowsCardReward(ClearRewardLimitCatalog? limits, int dungeonId, int containerItemId,
        DungeonIncountUsage dungeonUsage, DungeonIncountUsage? itemUsage = null)
    {
        if (limits is null) return true;
        if (limits.FindDungeon(dungeonId) is { } dungeonLimit && !CanGrantDungeonReward(dungeonLimit, dungeonUsage))
            return false;
        if (containerItemId > 0 && limits.FindItem(containerItemId) is { } itemLimit &&
            !CanGrantItemReward(itemLimit, itemUsage ?? dungeonUsage))
            return false;
        return true;
    }
}
