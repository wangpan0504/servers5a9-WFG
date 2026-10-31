using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 当前 PVF 里 [reward type] [expert job] 只有四条第一步：奖励整数恰好一个，1 附魔、2 炼金、3 分解、4 控偶。
internal static class QuestExpertJobReward
{
    public static bool Chain(StoryQuest quest) =>
        quest.Path.StartsWith("n_quest/expertjob/", StringComparison.OrdinalIgnoreCase);

    public static byte? Type(StoryQuest quest)
    {
        if (quest.RewardType != "[expert job]" || quest.Definition?.SelectRewards.Length > 0)
            return null;
        if (quest.Rewards is not [{ Type: 0, Text: null, Value: >= 1 and <= 4 }])
            return null;
        return (byte)quest.Rewards[0].Value;
    }
}
