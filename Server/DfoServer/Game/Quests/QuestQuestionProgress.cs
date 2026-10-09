using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestQuestionProgress
{
    // Full03 02039F90取实际选项，0203A104按零基序号发33；完成位序号+1沿用两端回答分支合同。
    // 未选择使用独立哨兵，不能把普通任务的初值1误认成第二个回答。
    public const uint Unanswered = uint.MaxValue;

    public static bool TryCount(StoryQuest quest,out int count)
    {
        count = 0;
        if (quest.Condition != "[question]" || quest.SubType != -1 || quest.IntData is not [> 0] ||
            quest.Definition is not { Diagnostics.Length: 0 } definition) return false;
        var info = definition.Fields.Where(field => field.Name == "question info").ToArray();
        if (info is not [{ Children.Length: 0 }] || info[0].Values.Length is < 3 or > 129 ||
            info[0].Values.Any(value => value.Type != 6 || value.Text is null)) return false;
        count = info[0].Values.Length-2;
        return true;
    }

    public static bool OrdinaryChoice(StoryQuest quest,uint answer,QuestCatalog quests) => TryCount(quest,out int count) && answer < count &&
        // 无后继的跳过/回放选项另走本版专用入口，不能经普通领奖完成整条剧情。
        quests.All.Any(next => next.Definition?.RequiredAnswers.Any(required => required.QuestId == quest.Id && required.Answer == answer) == true);
}
