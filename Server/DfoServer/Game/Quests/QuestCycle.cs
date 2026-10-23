using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCycle
{
    public static bool Daily(StoryQuest quest) => quest.Grade is "[daily]" or "[special daily]" ||
        QuestAutoDailyPlan.Supports(quest) || NormalDungeonMission(quest);

    // 已核对的普通安徒恩/卢克材料任务，复用本服每日领取账本；不开放自动分配的auto daily mission。
    internal static bool NormalDungeonMission(StoryQuest quest) => quest is
        { Id: 2683 or 10019 or 2902, Grade: "[daily mission]", Condition: "[seeking]", SubType: -1 or 0,
            MainQuest: null, RewardType: "[item]", CompleteNpc: -1,
            Definition: { Diagnostics.Length: 0, AccountQuest: false, Drops.Length: > 0, Dungeons.Length: > 0 } definition } &&
        QuestLifecyclePlan.ValidSeeking(quest) && definition.Drops.All(row => row.EnemyType == 1 && row.TemplateId > 0 &&
            row.Difficulty == -1 && row.Rate == 100 && row.Count > 0 && row.MaximumHeld > 0 &&
            definition.Dungeons.Any(dungeon => dungeon.DungeonId == row.DungeonId) &&
            Enumerable.Range(0, quest.IntData.Count / 2).Any(at => quest.IntData[at * 2] == row.ItemId &&
                quest.IntData[at * 2 + 1] == row.MaximumHeld));
    public static bool Repeat(StoryQuest quest) => quest.Grade == "[normaly repeat]";

    // 参考两端的游戏日：UTC+8 06:00，00:00–05:59仍属于上一天。
    public static int Day(DateTimeOffset now)
    {
        var day = now.UtcDateTime.AddHours(2);
        return day.Year * 10000 + day.Month * 100 + day.Day;
    }

    public static bool Same(Character first, Character second) => first.QuestCycleDay == second.QuestCycleDay &&
        first.AccountCompletedQuests.SequenceEqual(second.AccountCompletedQuests) &&
        SameAnswers(first.QuestAnswers,second.QuestAnswers) && SameAnswers(first.AccountQuestAnswers,second.AccountQuestAnswers) &&
        SameClaims(first.QuestDailyClaims, second.QuestDailyClaims) && SameClaims(first.AccountQuestDailyClaims, second.AccountQuestDailyClaims);

    public static bool SameAnswers(IReadOnlyDictionary<int,byte> first,IReadOnlyDictionary<int,byte> second) => first.Count == second.Count &&
        first.All(row => second.TryGetValue(row.Key,out byte answer) && answer == row.Value);

    public static bool SameClaims(IReadOnlyDictionary<int, int> first, IReadOnlyDictionary<int, int> second) => first.Count == second.Count &&
        first.All(row => second.TryGetValue(row.Key, out int day) && day == row.Value);
}
