using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestAccountPolicy
{
    // 本服明确规则：带奖同步会面任务每账号每任务领奖一次，首个合法交付角色取得原奖励。
    // 保留PVF的account quest与synchronization account quest区别，不改写资源声明。
    public static bool PaidSynchronization(StoryQuest quest) =>
        quest is { Grade: "[side]", Condition: "[meet npc]", SubType: -1, MainQuest: null, Npc: > 0, IntData.Count: 1, RewardType: "[item]" } &&
        quest.IntData[0] == (quest.CompleteNpc > 0 ? quest.CompleteNpc : quest.Npc) && !QuestCycle.Daily(quest) && !QuestCycle.Repeat(quest) &&
        quest.Definition is { Diagnostics.Length: 0, AccountQuest: false, JobChange: 0, AcceptanceRewards.Length: 0, SelectRewards.Length: 0 } definition &&
        !definition.Has("casecade clear") && !QuestMailPlan.HasMail(quest) &&
        definition.Fields.Where(field => field.Name == "synchronization account quest").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 1, Text: null }] }];

    public static bool SharedCompletion(StoryQuest quest) => quest.Definition?.AccountQuest == true || PaidSynchronization(quest);
}
