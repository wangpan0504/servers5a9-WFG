using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestDungeonClearTarget(int DungeonId, int MinimumDifficulty, uint Count);

// Full03类型1/sub6不附加评分或时间条件；33的value0只是请求读取权威通关次数。
internal static class QuestDungeonClearProgress
{
    public static bool TryTarget(StoryQuest quest, out QuestDungeonClearTarget target)
    {
        target = default;
        if (quest.Condition != "[condition under clear]" || quest.SubType != 6 || quest.IntData.Count is not (2 or 3) ||
            quest.IntData[0] <= 0 || quest.IntData[1] is < -1 or > 4 || quest.IntData.Count == 3 && quest.IntData[2] <= 0)
            return false;
        target = new(quest.IntData[0],quest.IntData[1],quest.IntData.Count == 3 ? checked((uint)quest.IntData[2]) : 1u);
        return true;
    }

    public static uint Initial(StoryQuest quest) => TryTarget(quest,out var target) ? target.Count :
        throw new NotSupportedException("条件通关需要已确认的sub6、正地下城、最低难度及次数声明");

    public static bool ValidScalar(StoryQuest quest, uint scalar) => TryTarget(quest,out var target) && scalar <= target.Count;

    public static bool Matches(StoryQuest quest, DungeonEntryPlan entry) => TryTarget(quest,out var target) &&
        target.DungeonId == entry.DungeonId && (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty);
}
