using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal sealed record QuestEligibilityMailPlan(Character Expected, ImmutableArray<QuestMailPlan> Mails)
{
    internal static bool Declaration(StoryQuest quest)
    {
        if (!QuestMailPlan.HasMail(quest) || quest.MainQuest is not null) return false;
        try
        {
            var declaration=QuestMailPlan.Read(quest);
            // 当前真实time0只有文字邀请；实体附件仍由完成邮件的完整物品规划负责。
            return declaration.Time==0 && declaration.Enclosures.IsEmpty && !QuestCycle.Repeat(quest);
        }
        catch (NotSupportedException) { return false; }
    }

    internal static QuestEligibilityMailPlan Prepare(Character expected, QuestCatalog catalog, DateTimeOffset now) =>
        new(expected,(expected.ActiveMainQuests.Length>=30 ? [] : catalog.All).Where(Declaration).Where(quest=>QuestEligibility.Evaluate(expected,quest,catalog).Eligible)
            .Select(quest=>new QuestMailPlan(quest.Id,quest.Path,QuestMailPlan.Read(quest),[],now)).ToImmutableArray());
}
