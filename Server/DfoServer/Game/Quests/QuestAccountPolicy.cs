using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestAccountPolicy
{
    // PVF 的显式账号同步声明独立于 [account quest]。只有无子节点、单个整数 1
    // 才能进入一次账号领奖事务；未知枚举、文本或复合声明继续保持未确认。
    public static bool ExplicitSynchronization(StoryQuest quest) =>
        quest.Definition is { Diagnostics.Length: 0, AccountQuest: false } definition &&
        definition.Fields.Where(field => field.Name == "synchronization account quest").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 1, Text: null }] }];

    // 本服明确规则：带奖同步会面任务每账号每任务领奖一次，首个合法交付角色取得原奖励。
    // 保留PVF的account quest与synchronization account quest区别，不改写资源声明。
    public static bool PaidSynchronization(StoryQuest quest) =>
        quest is { Grade: "[side]", Condition: "[meet npc]", SubType: -1, MainQuest: null, Npc: > 0, IntData.Count: 1, RewardType: "[item]" } &&
        quest.IntData[0] == (quest.CompleteNpc > 0 ? quest.CompleteNpc : quest.Npc) && !QuestCycle.Daily(quest) && !QuestCycle.Repeat(quest) &&
        quest.Definition is { Diagnostics.Length: 0, AccountQuest: false, JobChange: 0, AcceptanceRewards.Length: 0, SelectRewards.Length: 0 } definition &&
        !definition.Has("casecade clear") && !QuestMailPlan.HasMail(quest) &&
        definition.Fields.Where(field => field.Name == "synchronization account quest").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 1, Text: null }] }];

    public static bool SharedCompletion(StoryQuest quest) =>
        quest.Definition?.AccountQuest == true || PaidSynchronization(quest) || ExplicitSynchronization(quest);
}
