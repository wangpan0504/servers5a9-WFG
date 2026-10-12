using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    bool FreezeQuestEffects(QuestQuickClearPlan plan,QuestCatalog quests,out string reason)
    {
        try
        {
            plan.PrepareEffects(questEffects,quests);
            reason="任务实际效果已冻结"; return true;
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException or OverflowException or KeyNotFoundException)
        { reason=error.Message; return false; }
    }

}
