using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestExperienceReward(string ResourcePath, string QuestPath, int NativeIndex,
    int BaseExperience, int RawAdditionalPercent, string DifficultyKey, int DifficultyWeight, uint Amount)
{
    public uint GrowthContractExperience { get; init; }
}

internal static class QuestExperiencePolicy
{
    public static QuestExperienceReward Prepare(Character character, StoryQuest quest, QuestRewardParameterCatalog parameters,
        out int penalty)
    {
        if (character.Job > 15 || character.Level is < 1 or > 95)
            throw new InvalidDataException("任务奖励角色等级或职业无效");
        if (quest.MinimumLevel is < 1 or > 95 || quest.DeclaredIgnoreLevelForExperience is not (null or 0 or 1) ||
            quest.Difficulty is null || !parameters.Difficulty.TryGetValue(quest.Difficulty, out int weight) || weight < 0)
            throw new NotSupportedException("本服通用任务经验需要完整原始difficulty和等级声明");
        bool ignore = quest.DeclaredIgnoreLevelForExperience == 1;
        int index = ignore ? character.Level : quest.MinimumLevel;
        var row = parameters.GetExperienceAtNativeIndex(index);
        if (row.Base < 0) throw new NotSupportedException("本服任务EXP基础不能为负");
        penalty = LevelPenalty(character.Level - quest.MinimumLevel, quest.Grade == "[epic]", ignore, parameters);
        // 本服逐步向下取整：difficulty百分比 → 正追加百分比 → 原三元组等级百分比。
        ulong amount = quest.Attributes.Contains("not give exp quest") ? 0 :
            checked((ulong)row.Base * (ulong)weight) / 100;
        amount = checked(amount * (ulong)(100L + row.NativeAdditionalPercent)) / 100;
        amount = checked(amount * (ulong)penalty) / 100;
        if (amount > int.MaxValue) throw new InvalidDataException("本服任务EXP超出显示范围");
        return new(QuestRewardParameterCatalog.ResourcePath, quest.Path, index,
            row.Base, row.RawAdditionalPercent, quest.Difficulty, weight, (uint)amount);
    }

    internal static int LevelPenalty(int difference, bool epic, bool ignore, QuestRewardParameterCatalog parameters)
    {
        if (ignore) return 100;
        var green = parameters.Penalty(epic, false);
        var grey = parameters.Penalty(epic, true);
        // 本服将灰色区间上界之后延续为灰色比例；不改变或截断资源三元组。
        if (difference >= grey.MinimumDifference) return grey.Percent;
        if (difference >= green.MinimumDifference && difference <= green.MaximumDifference) return green.Percent;
        if (parameters.BasicPenalty < 0) throw new InvalidDataException("基础等级惩罚不能为负");
        return parameters.BasicPenalty;
    }

    // 共用同一计算；不按任务编号选择经验规则。
    public static QuestExperienceReward Prepare(Character character, StoryQuest quest, QuestRewardParameterCatalog parameters)
        => Prepare(character, quest, parameters, out _);
}
