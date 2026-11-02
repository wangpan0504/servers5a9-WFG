using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassEnhancementSource
{
    // 只接已核六项定义；便携器作为非券是本服策略，幸运券和固定/逐级/随机券没有此事实。
    public static bool Matches(BattlePassMission definition, EquipmentEnhancementFact fact)
    {
        var result = fact.Result;
        bool reinforce = result.Mode == EnhancementMode.Reinforce;
        bool amplify = result.Mode == EnhancementMode.Amplify;
        return definition switch
        {
            { Id: 17, RawTypeCode: 16, RequiredCount: 1, Experience: 100, RawParameter: -1 } => reinforce,
            { Id: 35, RawTypeCode: 16, RequiredCount: 3, Experience: 1000, RawParameter: -1 } => reinforce,
            { Id: 61, RawTypeCode: 16, RequiredCount: 30, Experience: 1600, RawParameter: -1 } => reinforce,
            { Id: 62, RawTypeCode: 38, RequiredCount: 30, Experience: 1600, RawParameter: -1 } => amplify,
            { Id: 63, RawTypeCode: 39, RequiredCount: 2, Experience: 1600, RawParameter: 10 } => reinforce && Reached(10),
            { Id: 64, RawTypeCode: 40, RequiredCount: 2, Experience: 1600, RawParameter: 7 } => amplify && Reached(7),
            _ => false
        };

        bool Reached(int level) => result.Succeeded && result.OutcomeCode == 0 &&
            result.OldLevel + 1 == result.NewLevel && result.NewLevel == level;
    }
}
