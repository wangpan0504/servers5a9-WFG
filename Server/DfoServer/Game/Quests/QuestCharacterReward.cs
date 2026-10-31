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
    // [inferno challenge]（7527/7530/7533/7539/7542）与 [cosmo challenge]（如10112“[超·星空裂缝]”值7191）同理：单个 Type0 Text null 且 Value>0 的奖励，行为与 hell challenge 相同（无资格状态，仅发经验/金币）。
    // 假设：任务6081“[星空裂缝]天空中出现的裂缝”奖励 [hell legend] 值291100268，无原服依据、值含义未知，仅限6081精确放行（形状：单个 Type0 Text null、无选择奖励），行为同深渊资格：只发经验/金币并记为已完成，不写资格状态。Refs #256
    public static bool TryHellChallenge(StoryQuest quest) =>
        (quest.RewardType == "[hell challenge]" && quest.Rewards is [{ Type: 0, Text: null, Value: 1 }] ||
            quest.RewardType is "[inferno challenge]" or "[cosmo challenge]" && quest.Rewards is [{ Type: 0, Text: null, Value: > 0 }] ||
            quest.Id == 6081 && quest.RewardType == "[hell legend]" && quest.Rewards is [{ Type: 0, Text: null, Value: 291100268 }]) &&
        quest.Definition is { SelectRewards.Length: 0 };

    // 假设（无原服依据，Refs #256）：深渊派对入口资格 = 角色已完成任一确认形态的 [hell challenge] 资格任务。
    // 服务端没有单独的资格表，完成态已随 CompletedQuests 持久化；客户端入口 UI 如何判定、深渊入场请求的包形状均未逆向确认，
    // 因此此函数只供将来的入场校验使用，目前不参与任何入场拒绝。
    public static bool HasHellPartyQualification(Character character, QuestCatalog quests) =>
        character.CompletedQuests.Any(id => quests.TryGet(id,out var quest) && quest.RewardType == "[hell challenge]" && TryHellChallenge(quest));

    public static byte EquipmentSlots(Character expected, StoryQuest quest)
    {
        if (quest.RewardType != "[slot expansion]") return expected.EquipmentSlotFlags;
        if (!TryScalar(quest,out int index)) throw new NotSupportedException("装备槽奖励需要本版0/1/2索引");
        // Full03 Player+1AC；02B64FA0按22/23/25检验1/2/10，耳环不能使用连续位4。
        return (byte)(expected.EquipmentSlotFlags | (index switch { 0 => 1, 1 => 2, _ => 16 }));
    }
}
