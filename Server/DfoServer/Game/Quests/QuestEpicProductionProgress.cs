using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 本版成功加工 ACK1419 分派事件44，对应任务条件67；只由已提交的加工计划推进，CMD33不能伪造。
internal static class QuestEpicProductionProgress
{
    public static bool Supports(StoryQuest quest) => quest is
        { Grade:"[side]",Condition:"[epic production]",SubType:-1,IntData.Count:0,MainQuest:null,
          Definition:{ Diagnostics.Length:0,AccountQuest:false } } && QuestCoverage.HasOrdinaryDeclarations(quest);
    public static ActiveMainQuest[] Refresh(ActiveMainQuest[] active,QuestCatalog quests) => active.Select(row =>
        row.ConditionState!=0 && quests.TryGet(row.QuestId,out var quest) && Supports(quest)
            ? row with { ConditionState=0 } : row).ToArray();
}
