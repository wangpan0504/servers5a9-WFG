using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassAvatarCompoundSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 68, RequiredCount: 6, Experience: 1600, RawTypeCode: 41, RawParameter: -1 };
}
