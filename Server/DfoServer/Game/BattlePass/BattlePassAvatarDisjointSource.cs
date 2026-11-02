using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassAvatarDisjointSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 69, RequiredCount: 6, Experience: 1600, RawTypeCode: 42, RawParameter: -1 };
}
