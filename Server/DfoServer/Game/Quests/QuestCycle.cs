using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCycle
{
    public static bool Daily(StoryQuest quest) => quest.Grade is "[daily]" or "[special daily]" || QuestAutoDailyPlan.Supports(quest);
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
