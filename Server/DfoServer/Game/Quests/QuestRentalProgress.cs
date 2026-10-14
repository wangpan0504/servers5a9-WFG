using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// Full03 的 ACK1001 成功事件17只指向Q8581，不把任意custom条件当作租赁事实。
internal static class QuestRentalProgress
{
    public const ushort QuestId = 8581;

    public static bool Supports(StoryQuest quest) => quest.Id == QuestId && quest.Grade == "[system]" &&
        quest.Condition == "[custom quest]" && quest.SubType == 0 && quest.IntData is [1, 0, 1] &&
        quest.Npc == 2 && quest.CompleteNpc == -1 && quest.MinimumLevel == 10 && quest.MaximumLevel == 99 &&
        quest.Definition is { AccountQuest: true, Diagnostics.Length: 0 } &&
        QuestCoverage.HasOrdinaryDeclarations(quest);
}
