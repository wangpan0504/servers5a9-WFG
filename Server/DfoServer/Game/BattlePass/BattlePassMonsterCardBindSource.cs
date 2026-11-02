using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassMonsterCardBindSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 21, RequiredCount: 1, Experience: 100, RawTypeCode: 21, RawParameter: -1 } or
        { Id: 34, RequiredCount: 2, Experience: 1000, RawTypeCode: 21, RawParameter: -1 } or
        { Id: 60, RequiredCount: 20, Experience: 1600, RawTypeCode: 21, RawParameter: -1 };
}
