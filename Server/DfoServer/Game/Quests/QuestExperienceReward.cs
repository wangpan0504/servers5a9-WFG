using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestExperienceReward(string RuleId, string ResourcePath, string QuestPath, int NativeIndex,
    int BaseExperience, int RawAdditionalPercent, string DifficultyKey, int DifficultyWeight, uint Amount);

internal static class QuestExperiencePolicy
{
    public const string RuleId = "local-3145-quest-parameter-exp-v1";
    public const string QuestPath = "n_quest/quest_renewal/epic_quest/elvengard/elvengard_epic_01.qst";

    // 本服明确采用difficulty百分比及原生直接index；不是已还原的原服普通任务经验公式。
    public static QuestExperienceReward Prepare(Character character, StoryQuest quest, QuestRewardParameterCatalog parameters)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(quest);
        ArgumentNullException.ThrowIfNull(parameters);
        if (character.Job > 15 || quest.Id != 3145 || quest.Path != QuestPath ||
            quest.Grade != "[epic]" || quest.Difficulty != "1" || quest.Condition != "[clear map]" || quest.SubType != -1 ||
            !quest.IntData.SequenceEqual([76126]) || quest.MinimumLevel != 1 || quest.MaximumLevel != 99 || quest.DungeonIndex != 3 ||
            quest.DungeonParameter != -1 || quest.Npc != 1 || quest.CompleteNpc != 1247 || quest.MainQuest is not null || quest.Prerequisites.Count != 0 ||
            quest.BossTemplate != 107000903 || quest.RewardType != "[item]" ||
            quest.Attributes.Contains("not give exp quest") || quest.DeclaredIgnoreLevelForExperience is not (null or 0))
            throw new NotSupportedException("任务经验仅支持已批准的全职业普通3145资源合同");
        _ = QuestFixedRewardGrant.ReadRewards(character.Job, quest);
        int index = quest.MinimumLevel;
        var row = parameters.GetExperienceAtNativeIndex(index);
        if (row.Base <= 0 || row.NativeAdditionalPercent != 0 || !parameters.Difficulty.TryGetValue(quest.Difficulty, out int weight) || weight <= 0)
            throw new NotSupportedException("本服3145任务经验不使用缺失difficulty、非正基础或未批准的追加倍率");
        ulong computed = checked((ulong)row.Base * (ulong)weight) / 100;
        if (computed is 0 or > int.MaxValue)
            throw new InvalidDataException("本服3145任务EXP超出匹配显示及成长提交范围");
        return new(RuleId, QuestRewardParameterCatalog.ResourcePath, quest.Path, index,
            row.Base, row.RawAdditionalPercent, quest.Difficulty, weight, checked((uint)computed));
    }
}
