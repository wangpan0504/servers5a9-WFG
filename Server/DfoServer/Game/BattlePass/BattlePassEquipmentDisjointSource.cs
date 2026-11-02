using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassEquipmentDisjointSource
{
    public static bool Matches(BattlePassMission definition) => definition.RawTypeCode == 17 && definition.RawParameter == -1 &&
        (definition.Id, definition.RequiredCount, definition.Experience) is (18, 3, 100) or (42, 9, 1000);
}
