using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestDungeonClearTarget(int DungeonId, int MinimumDifficulty, uint Count,
    int? MinimumInitializations = null);

// sub6是剩余通关次数；sub11保持布尔scalar，真实结算时核对本次实例的地图初始化统计。
internal static class QuestDungeonClearProgress
{
    public static bool TryTarget(StoryQuest quest, out QuestDungeonClearTarget target)
    {
        target = default;
        if (quest.Condition != "[condition under clear]" || quest.SubType is not (6 or 11) || quest.IntData.Count is not (2 or 3) ||
            quest.IntData[0] <= 0 || quest.IntData[1] is < -1 or > 4)
            return false;
        if (quest.SubType == 11)
        {
            if (quest.IntData.Count != 3 || quest.IntData[2] != -1 && quest.IntData[2] <= 0) return false;
            target = new(quest.IntData[0],quest.IntData[1],1u,quest.IntData[2]);
            return true;
        }
        if (quest.IntData.Count == 3 && quest.IntData[2] <= 0) return false;
        target = new(quest.IntData[0],quest.IntData[1],quest.IntData.Count == 3 ? checked((uint)quest.IntData[2]) : 1u);
        return true;
    }

    public static uint Initial(StoryQuest quest) => TryTarget(quest,out var target) ? target.Count :
        throw new NotSupportedException("条件通关需要已确认的sub6/sub11、正地下城、最低难度及目标声明");

    public static bool ValidScalar(StoryQuest quest, uint scalar) => TryTarget(quest,out var target) && scalar <= target.Count;

    public static bool CountsInitializations(StoryQuest quest, DungeonEntryPlan entry) =>
        TryTarget(quest,out var target) && target.MinimumInitializations is not null && MatchesDungeon(target,entry);

    public static bool Matches(StoryQuest quest, DungeonEntryPlan entry, SinglePlayerDungeonRun? run = null)
    {
        if (!TryTarget(quest,out var target) || !MatchesDungeon(target,entry)) return false;
        if (target.MinimumInitializations is not { } minimum) return true;
        if (run is not { InitializationCounterSupported: true }) return false;
        uint? threshold = minimum == -1 ? run.MazeInitializationThreshold : checked((uint)minimum);
        return threshold is { } count && run.ConfirmedInitializationCount >= count;
    }

    static bool MatchesDungeon(QuestDungeonClearTarget target, DungeonEntryPlan entry) =>
        target.DungeonId == entry.DungeonId && (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty);
}
