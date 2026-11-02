using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassEquipmentEnchantSource
{
    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 30, RequiredCount: 2, Experience: 1000, RawTypeCode: 27, RawParameter: -1 };
}
