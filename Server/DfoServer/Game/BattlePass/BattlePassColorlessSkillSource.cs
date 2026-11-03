using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

// 日任务15：使用消耗无色小晶块（3037）的技能，5次、100经验。一次施法计1，不按晶块个数放大。
internal static class BattlePassColorlessSkillSource
{
    public const int ItemId = 3037;

    public static bool Matches(BattlePassMission definition) => definition is
        { Id: 15, RawTypeCode: 15, RawParameter: ItemId, RequiredCount: 5, Experience: 100 };
}
