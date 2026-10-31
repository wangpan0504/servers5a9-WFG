using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Quests;

// Full03 NOTI21候选→0228B7E0→884；带条件队列的隐藏资源另有实际触发入口。
internal static class QuestHiddenEntry
{
    public static bool ImmediateShape(StoryQuest quest) => quest.Grade == "[hidden]" && quest.MainQuest is null &&
        quest.Definition is { FirstExposedByNpc: null } definition && !definition.Fields.Any(field =>
            field.Name is "area" or "npcdialog" or "standbyarea" or "itemusecheck" or
                "npc dialog" or "stand by area" or "item use check" or "reach the range" or "hidden set");

    // 匹配客户端0399A410把实际对话NPC传给02289BE0的type1；02289DE9精确比较QST+2A0。
    // 884只是回传资源字段，不是可信的对话完成事实；会话还须验证当前真实NPC场景。
    public static bool NpcDialogueShape(StoryQuest quest) => quest is
        { Grade: "[hidden]", MainQuest: null, Condition: "[meet npc]", SubType: -1, IntData: [> 0] } &&
        quest.Definition is { FirstExposedByNpc: null } definition &&
        definition.Fields.Where(field => field.Name == "npc dialog").ToArray() is
            [{ Values: [{ Type: 0, Value: > 0 }], Children.Length: 0 }] &&
        !definition.Fields.Any(field => field.Name is "area" or "npcdialog" or "standbyarea" or "itemusecheck" or
            "stand by area" or "item use check" or "reach the range" or "hidden set");

    // cleared quest 是原客户端接取后自动请求33的存在标志，不是可信完成事实。
    // 只兼容已有NPC对话入口且目标相同的零参数声明，仍由会话验证实际NPC场景。
    public static bool ClearedNpcDialogueShape(StoryQuest quest) => NpcDialogueShape(quest) &&
        quest.Definition!.Fields.Where(field => field.Name == "cleared quest").ToArray() is
            [{ Values.Length: 0, Children.Length: 0 }] &&
        quest.Definition.Values("npc dialog")[0].Value == quest.IntData[0];

    public static bool Direct(StoryQuest quest, QuestCatalog catalog) =>
        (ImmediateShape(quest) || NpcDialogueShape(quest) || QuestStandbyEntry.Supports(quest)) &&
        QuestCoverage.HasOrdinaryDeclarations(quest) && QuestCoverage.HasRequiredReferences(quest,catalog) &&
        QuestCoverage.HasSupportedTarget(quest,catalog);

    public static bool CanOpen(Character character, StoryQuest quest, QuestCatalog catalog) => ImmediateShape(quest) && Direct(quest,catalog) &&
        QuestEligibility.Evaluate(character,quest,catalog).Eligible &&
        !quest.Definition!.HideOnCompletedQuests.Any(QuestEligibility.Completed(character).Contains);
    public static bool CanOpenNpcDialogue(Character character, StoryQuest quest, QuestCatalog catalog,
        TownCatalog towns, uint npcId) => NpcDialogueShape(quest) && Direct(quest,catalog) &&
        quest.Definition!.Values("npc dialog")[0].Value == npcId &&
        QuestEligibility.Evaluate(character,quest,catalog).Eligible &&
        !quest.Definition.HideOnCompletedQuests.Any(QuestEligibility.Completed(character).Contains) &&
        towns.IsPosition(character.Position,QuestEligibility.Completed(character)) &&
        towns.Areas.TryGetValue((character.Position.Town,character.Position.Area),out var area) &&
        area.Map(QuestEligibility.Completed(character)).Npcs.Any(npc => npc.Id == npcId);
}
