using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestCycle
{
    public static bool Daily(StoryQuest quest) => quest.Grade is "[daily]" or "[special daily]" ||
        QuestAutoDailyPlan.Supports(quest) || NormalDungeonMission(quest);

    // 已核对的普通安徒恩/卢克材料任务，复用本服每日领取账本；不开放自动分配的auto daily mission。
    internal static bool NormalDungeonMission(StoryQuest quest) => AdditionalDungeonMission(quest) || quest is
        { Id: 2683 or 10019 or 2902, Grade: "[daily mission]", Condition: "[seeking]", SubType: -1 or 0,
            MainQuest: null, RewardType: "[item]", CompleteNpc: -1,
            Definition: { Diagnostics.Length: 0, AccountQuest: false, Drops.Length: > 0, Dungeons.Length: > 0 } definition } &&
        QuestLifecyclePlan.ValidSeeking(quest) && definition.Drops.All(row => row.EnemyType == 1 && row.TemplateId > 0 &&
            row.Difficulty == -1 && row.Rate == 100 && row.Count > 0 && row.MaximumHeld > 0 &&
            definition.Dungeons.Any(dungeon => dungeon.DungeonId == row.DungeonId) &&
            Enumerable.Range(0, quest.IntData.Count / 2).Any(at => quest.IntData[at * 2] == row.ItemId &&
                quest.IntData[at * 2 + 1] == row.MaximumHeld));
    // 这批原资源使用普通材料、猎杀或通关合同；周期沿既有本服每日账本，不能推广到自动分配或决斗每日。
    private static bool AdditionalDungeonMission(StoryQuest quest)
    {
        if (quest is not { Grade: "[daily mission]", MainQuest: null, RewardType: "[item]",
            Definition: { Diagnostics.Length: 0, AccountQuest: false, ExposedByNpc: true } definition }) return false;
        bool seeking = quest.Condition == "[seeking]" && QuestLifecyclePlan.ValidSeeking(quest);
        return quest.Id switch
        {
            2901 => seeking && quest.SubType == 0 && quest.IntData is [10159408,1,10159409,1,10159410,1,10159411,1,10159412,1] &&
                definition.Drops.Length == 5 && ValidMaterialDrops(),
            4099 => seeking && quest.SubType == -1 && quest.IntData is [4730,12] && definition.Drops.Length > 0 && ValidMaterialDrops(),
            4100 => seeking && quest.SubType == -1 && quest.IntData is [4731,12] && definition.Drops.Length > 0 && ValidMaterialDrops(),
            4111 => seeking && quest.SubType == -1 && quest.IntData is [3329,10] && definition.Drops.Length == 0,
            4112 => seeking && quest.SubType == -1 && quest.IntData is [3323,10] && definition.Drops.Length == 0,
            8557 => IsMoonMountainDailyMission(quest),
            8714 => seeking && quest.SubType == -1 && quest.IntData is [490014160,2] && definition.Drops.Length > 0 && ValidMaterialDrops(),
            6117 => quest.Condition == "[hunt monster]" && quest.SubType == -1 && quest.IntData is [7322,-1,-3,1],
            2942 => quest.Condition == "[hunt enemy]" && quest.SubType == -1 && quest.IntData is [5097,-1,53764,3,3,5098,-1,107001865,1,1],
            8926 => quest.Condition == "[condition under clear]" && quest.SubType == 6 && quest.IntData is [400000046,-1],
            _ => false
        };

        bool ValidMaterialDrops() => definition.Drops.All(row => row.EnemyType == 1 && row.TemplateId > 0 && row.DungeonId > 0 &&
            row.Difficulty == -1 && row.Rate is > 0 and <= 100 && row.Count > 0 && (row.MaximumHeld == -1 || row.MaximumHeld > 0) &&
            Enumerable.Range(0,quest.IntData.Count / 2).Any(at => quest.IntData[at * 2] == row.ItemId));
    }

    // #1186：8557“进攻准备”要求前往月轮山收集未精炼的念气结晶（10094602×10），当前PVF的QST/MOB/独立掉落均无该任务材料产源。
    internal static bool IsMoonMountainDailyMission(StoryQuest quest) =>
        quest is { Id: 8557, Grade: "[daily mission]", Condition: "[seeking]", SubType: -1, MainQuest: null,
            RewardType: "[item]", IntData: [10094602, 10],
            Definition: { Diagnostics.Length: 0, AccountQuest: false, ExposedByNpc: true, Drops.Length: 0, Dungeons.Length: 0 } } &&
        QuestLifecyclePlan.ValidSeeking(quest);

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
