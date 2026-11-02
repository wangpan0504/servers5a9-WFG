using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassRepairSource
{
    public static bool Matches(BattlePassMission definition) => definition.RawTypeCode == 18 && definition.RawParameter == -1 &&
        (definition is { Id: 19, RequiredCount: 1, Experience: 100 } || definition is { Id: 47, RequiredCount: 3, Experience: 1000 });
}
