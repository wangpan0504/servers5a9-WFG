using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassNpcGiftSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 24, RequiredCount: 1, Experience: 100, RawTypeCode: 25, RawParameter: -1 };
}
