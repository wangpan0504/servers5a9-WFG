using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCharacterReward
{
    // 匹配PVF的[tp]没有数量数据；Full03 ACK34 mode29固定显示2点，不读取物品奖励数组。
    public static bool TryTp(StoryQuest quest, out ushort amount)
    {
        amount = 0;
        if (quest.RewardType != "[tp]" || quest.Rewards.Count != 0 || quest.Definition is not { SelectRewards.Length: 0 } ||
            quest.Definition.Fields.Where(field => field.Name is "reward int data" or "reward selection int data")
                .Any(field => field.Values.Length != 0 || field.Children.Length != 0)) return false;
        amount = 2;
        return true;
    }

    public static CharacterLearningState AddTp(CharacterLearningState learning, ushort amount)
    {
        learning.Validate();
        if (!learning.Initialized || learning.BonusTp is null)
            throw new NotSupportedException("任务TP奖励需要已核算的双树额外TP来源");
        // 额外来源与两树余额同时增加；不重置已花费点数、技能、指令或连招。
        var result = learning with
        {
            BonusTp = checked((ushort)(learning.BonusTp.Value + amount)),
            Trees = Array.AsReadOnly(learning.Trees.Select(tree =>
                (tree with { Tp = checked((ushort)(tree.Tp + amount)) }).Freeze()).ToArray())
        };
        result.Validate();
        return result;
    }

    public static bool TryScalar(StoryQuest quest, out int value)
    {
        value = 0;
        if (quest.Definition is not { SelectRewards.Length: 0 } || quest.Rewards is not [{ Type: 0, Text: null }]) return false;
        value = quest.Rewards[0].Value;
        return quest.RewardType == "[slot expansion]" && value is >= 0 and <= 2;
    }

    // 深渊派对挑战资格类任务只有参数1的形态已核对：奖励只剩经验/金币，服务端没有资格状态，完成即记入已完成。
    public static bool TryHellChallenge(StoryQuest quest) =>
        quest.RewardType == "[hell challenge]" && quest.Rewards is [{ Type: 0, Text: null, Value: 1 }] &&
        quest.Definition is { SelectRewards.Length: 0 };

    public static byte EquipmentSlots(Character expected, StoryQuest quest)
    {
        if (quest.RewardType != "[slot expansion]") return expected.EquipmentSlotFlags;
        if (!TryScalar(quest,out int index)) throw new NotSupportedException("装备槽奖励需要本版0/1/2索引");
        // Full03 Player+1AC；02B64FA0按22/23/25检验1/2/10，耳环不能使用连续位4。
        return (byte)(expected.EquipmentSlotFlags | (index switch { 0 => 1, 1 => 2, _ => 16 }));
    }
}
