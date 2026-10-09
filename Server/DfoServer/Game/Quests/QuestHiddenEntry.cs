using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// Full03 NOTI21候选→0228B7E0→884；带条件队列的隐藏资源另有实际触发入口。
internal static class QuestHiddenEntry
{
    public static bool ImmediateShape(StoryQuest quest) => quest.Grade == "[hidden]" && quest.MainQuest is null &&
        quest.Definition is { FirstExposedByNpc: null } definition && !definition.Fields.Any(field =>
            field.Name is "area" or "npcdialog" or "standbyarea" or "itemusecheck" or
                "npc dialog" or "stand by area" or "item use check" or "reach the range" or "hidden set");

    public static bool Direct(StoryQuest quest, QuestCatalog catalog) => ImmediateShape(quest) &&
        QuestCoverage.HasOrdinaryDeclarations(quest) && QuestCoverage.HasRequiredReferences(quest,catalog) &&
        QuestCoverage.HasSupportedTarget(quest,catalog);

    public static bool CanOpen(Character character, StoryQuest quest, QuestCatalog catalog) => Direct(quest,catalog) &&
        QuestEligibility.Evaluate(character,quest,catalog).Eligible &&
        !quest.Definition!.HideOnCompletedQuests.Any(QuestEligibility.Completed(character).Contains);
}
