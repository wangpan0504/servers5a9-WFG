using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal static class BattlePassEmblemSource
{
    public static bool Matches(BattlePassMission definition, EmblemCompoundFact fact)
    {
        if (definition.RawTypeCode == 19 && definition.RawParameter == -1)
            return definition is { Id: 20, RequiredCount: 1, Experience: 100 } or
                { Id: 44, RequiredCount: 3, Experience: 1000 } or { Id: 65, RequiredCount: 20, Experience: 1600 };
        if (definition.RawTypeCode != 20 || definition.RequiredCount != 2 || definition.Experience != 1600) return false;
        // 原池按最低输入grade选池，参数为目标grade；白金或跨级不是两种恰升一级。
        return definition.Id switch
        {
            66 => definition.RawParameter == 2 && fact.Receipt.MinimumGrade == 1 && fact.Reward is { Grade: 2, Rarity: 2 },
            67 => definition.RawParameter == 3 && fact.Receipt.MinimumGrade == 2 && fact.Reward is { Grade: 3, Rarity: 3 },
            _ => false
        };
    }
}
