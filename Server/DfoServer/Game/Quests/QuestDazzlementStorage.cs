using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 眩惑之塔 6401–6403 的精确形状。客户端类型 60 只在 int data 第 4 项为 1 时走物品捷径；这三条第 4 项是 0。
internal static class QuestDazzlementStorage
{
    public static bool Matches(StoryQuest quest) => quest.Condition == "[custom multi condition]" && quest.SubType == 1 &&
        quest.IntData.SequenceEqual([1, 0, 0, 0]) && quest.Npc > 0 && quest.CompleteNpc < 0 &&
        quest.Definition is { Diagnostics.Length: 0 };
}
