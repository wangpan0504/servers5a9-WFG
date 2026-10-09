using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCharacterReward
{
    public static bool TryScalar(StoryQuest quest, out int value)
    {
        value = 0;
        if (quest.Definition is not { SelectRewards.Length: 0 } || quest.Rewards is not [{ Type: 0, Text: null }]) return false;
        value = quest.Rewards[0].Value;
        return quest.RewardType == "[slot expansion]" && value is >= 0 and <= 2;
    }

    public static byte EquipmentSlots(Character expected, StoryQuest quest)
    {
        if (quest.RewardType != "[slot expansion]") return expected.EquipmentSlotFlags;
        if (!TryScalar(quest,out int index)) throw new NotSupportedException("装备槽奖励需要本版0/1/2索引");
        // Full03 Player+1AC；02B64FA0按22/23/25检验1/2/10，耳环不能使用连续位4。
        return (byte)(expected.EquipmentSlotFlags | (index switch { 0 => 1, 1 => 2, _ => 16 }));
    }
}
