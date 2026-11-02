using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassNpcMaterialConsumeSource
{
    public static int Material(int npcId) => npcId switch { 476 => 10158964, 609 => 10306130, _ => 0 };

    public static bool Matches(byte category, BattlePassMission definition, int npcId, int materialId) =>
        category == 1 && definition is { RequiredCount: 1, Experience: 1000, RawTypeCode: 28 } &&
        (definition.Id, npcId, materialId, definition.RawParameter) is
            (31, 476, 10158964, 10158964) or (32, 609, 10306130, 10306130);

    public static long NextCount(byte category, BattlePassMission definition, int npcId, int materialId, long current, int amount)
    {
        if (!Matches(category, definition, npcId, materialId) || current < 0 || current > definition.RequiredCount || amount <= 0)
            throw new InvalidDataException("指定商人材料消费任务与原定义或实际数量不一致");
        return Math.Min(definition.RequiredCount, current + (long)amount);
    }
}
