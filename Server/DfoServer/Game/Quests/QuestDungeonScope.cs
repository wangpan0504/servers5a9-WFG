using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestDungeonScope
{
    public static bool CanEvaluate(QuestDungeonSelector target, StoryQuest quest) => target.Kind switch
    {
        // dungeon info是界面指引，不能代替区域成员表或过滤条件。
        QuestDungeonSelectorKind.FilteredArea or QuestDungeonSelectorKind.Area => false,
        _ => true
    };

    public static bool Matches(QuestDungeonSelector target, StoryQuest quest, DungeonEntryPlan entry)
    {
        if (target.Difficulty >= 0 && entry.Difficulty < target.Difficulty || !CanEvaluate(target, quest)) return false;
        var limits = QuestTargetSchema.Read(quest).DungeonLimits;
        if (limits.Length != 0 && !limits.Contains(checked((int)entry.DungeonId))) return false;
        return target.Kind switch
        {
            QuestDungeonSelectorKind.Any => true,
            QuestDungeonSelectorKind.Dungeon => target.Id == entry.DungeonId,
            QuestDungeonSelectorKind.Recommended => entry.RecommendedQuestClear,
            _ => false
        };
    }
}
