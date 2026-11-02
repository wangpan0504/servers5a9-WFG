using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassGuildContributionSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 9, RawTypeCode: 9, RawParameter: -1, RequiredCount: 150, Experience: 100 };

    public static long NextCount(BattlePassMission definition, long current, int points)
    {
        if (!Matches(definition) || current < 0 || current > definition.RequiredCount || points <= 0)
            throw new InvalidDataException("公会贡献任务增量与原定义不一致");
        return Math.Min(definition.RequiredCount, current + (long)points);
    }
}
