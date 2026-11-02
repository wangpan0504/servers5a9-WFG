using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassMonsterCardUpgradeSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 45, RequiredCount: 3, Experience: 1000, RawTypeCode: 31, RawParameter: -1 } or
        { Id: 70, RequiredCount: 30, Experience: 1600, RawTypeCode: 31, RawParameter: -1 };
}
