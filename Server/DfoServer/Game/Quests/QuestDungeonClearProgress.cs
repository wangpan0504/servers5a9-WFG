using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal readonly record struct QuestDungeonClearTarget(int DungeonId, int MinimumDifficulty, uint Count,
    int? MinimumInitializations = null);

// 类型1/sub6是剩余次数；类型45/sub6是列表任一通关，sub11核对本实例的初始化统计。
internal static class QuestDungeonClearProgress
{
    // 推荐地下城沿本服已有95版推荐资格，计数仍只由原接取绑定的真实清图推进。
    // 自动每日仍由周期入口分配，不改为手工可接，也不将任意sub17参数解释为推荐通关。
    internal static bool Recommended(StoryQuest quest) => quest is
        { Grade: "[mission]" or "[auto daily mission]", Condition: "[condition under clear]", SubType: 17, IntData: [-1,-1,> 0] };

    public static bool Supports(StoryQuest quest) => QuestMarriageProgress.Preparation(quest) || Recommended(quest) || TryTarget(quest,out _) || HasDungeonAlternatives(quest);

    // 两份原任务文本明确每天只累计一次；不把这个限制推广到其它sub6任务。
    public static bool CountOncePerDay(StoryQuest quest) => quest.SubType == 6 && TryTarget(quest,out var target) &&
        target.Count == 5 && target.MinimumDifficulty == -1 &&
        (quest.Id == 13200 && target.DungeonId == 9607 || quest.Id == 8683 && target.DungeonId == 400000016);

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

    public static uint Initial(StoryQuest quest) => Recommended(quest) || QuestMarriageProgress.Preparation(quest) ? checked((uint)quest.IntData[2]) : TryTarget(quest,out var target) ? target.Count :
        HasDungeonAlternatives(quest) ? 1u :
        throw new NotSupportedException("通关需要已确认的sub6/sub11、正地下城、最低难度及目标声明");

    public static bool ValidScalar(StoryQuest quest, uint scalar) => Recommended(quest) || QuestMarriageProgress.Preparation(quest) ? scalar <= quest.IntData[2] : TryTarget(quest,out var target) ? scalar <= target.Count :
        HasDungeonAlternatives(quest) && scalar <= 1;

    // 难度在真实ClearFact产生时核对；领奖凭据复核同一接取及原目标地下城。
    public static bool ContainsDungeon(StoryQuest quest, int dungeonId)
    {
        if (Recommended(quest) || QuestMarriageProgress.Preparation(quest)) return dungeonId > 0;
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
        if (QuestMarriageProgress.Preparation(quest)) return QuestMarriageProgress.Matches(entry, run);
        if (Recommended(quest)) return entry.RecommendedQuestClear;
        if (HasDungeonAlternatives(quest))
            return MatchesAlternative(quest,entry.DungeonId,entry.Difficulty);
        if (!TryTarget(quest,out var target) || !MatchesDungeon(target,entry)) return false;
        if (target.MinimumInitializations is not { } minimum) return true;
        if (run is not { InitializationCounterSupported: true }) return false;
        uint? threshold = minimum == -1 ? run.MazeInitializationThreshold : checked((uint)minimum);
        return threshold is { } count && run.ConfirmedInitializationCount >= count;
    }

    // 入场和真实清图共用原生45的OR匹配，每一对保留自己的最低难度。
    public static bool MatchesAlternative(StoryQuest quest, uint dungeonId, byte difficulty)
    {
        if (!HasDungeonAlternatives(quest)) return false;
        for (int at = 0; at < quest.IntData.Count; at += 2)
            if (quest.IntData[at] == dungeonId &&
                (quest.IntData[at+1] == -1 || difficulty >= quest.IntData[at+1])) return true;
        return false;
    }

    static bool MatchesDungeon(QuestDungeonClearTarget target, DungeonEntryPlan entry) =>
        target.DungeonId == entry.DungeonId && (target.MinimumDifficulty == -1 || entry.Difficulty >= target.MinimumDifficulty);
}
