using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestDungeonClearTarget(int DungeonId, int MinimumDifficulty, uint Count,
    int? MinimumInitializations = null);

// 类型1/sub6是剩余次数；类型45/sub6是列表任一通关，sub11核对本实例的初始化统计。
internal static class QuestDungeonClearProgress
{
    public static bool Supports(StoryQuest quest) => TryTarget(quest,out _) || HasDungeonAlternatives(quest);

    static bool HasDungeonAlternatives(StoryQuest quest)
    {
        if (quest.Condition != "[clear a dungeon]" || quest.SubType != 6 || quest.IntData.Count < 2 || quest.IntData.Count % 2 != 0)
            return false;
        for (int at = 0; at < quest.IntData.Count; at += 2)
            if (quest.IntData[at] <= 0 || quest.IntData[at+1] is < -1 or > 4) return false;
        return true;
    }

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
        HasDungeonAlternatives(quest) ? 1u :
        throw new NotSupportedException("通关需要已确认的sub6/sub11、正地下城、最低难度及目标声明");

    public static bool ValidScalar(StoryQuest quest, uint scalar) => TryTarget(quest,out var target) ? scalar <= target.Count :
        HasDungeonAlternatives(quest) && scalar <= 1;

    // 难度在真实ClearFact产生时核对；领奖凭据复核同一接取及原目标地下城。
    public static bool ContainsDungeon(StoryQuest quest, int dungeonId)
    {
        if (TryTarget(quest,out var target)) return target.DungeonId == dungeonId;
        if (!HasDungeonAlternatives(quest)) return false;
        for (int at = 0; at < quest.IntData.Count; at += 2)
            if (quest.IntData[at] == dungeonId) return true;
        return false;
    }

    public static bool CountsInitializations(StoryQuest quest, DungeonEntryPlan entry) =>
        TryTarget(quest,out var target) && target.MinimumInitializations is not null && MatchesDungeon(target,entry);

    public static bool Matches(StoryQuest quest, DungeonEntryPlan entry, SinglePlayerDungeonRun? run = null)
    {
        if (HasDungeonAlternatives(quest))
        {
            // 原生45的成对列表是OR；每一对都保留自己的最低难度，不用首项代替整表。
            for (int at = 0; at < quest.IntData.Count; at += 2)
                if (quest.IntData[at] == entry.DungeonId &&
                    (quest.IntData[at+1] == -1 || entry.Difficulty >= quest.IntData[at+1])) return true;
            return false;
        }
        if (!TryTarget(quest,out var target) || !MatchesDungeon(target,entry)) return false;
        if (target.MinimumInitializations is not { } minimum) return true;
        if (run is not { InitializationCounterSupported: true }) return false;
        uint? threshold = minimum == -1 ? run.MazeInitializationThreshold : checked((uint)minimum);
        return threshold is { } count && run.ConfirmedInitializationCount >= count;
    }

    static bool MatchesDungeon(QuestDungeonClearTarget target, DungeonEntryPlan entry) =>
        target.DungeonId == entry.DungeonId && (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty);
}
