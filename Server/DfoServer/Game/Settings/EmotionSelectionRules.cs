using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Settings;

internal static class EmotionSelectionRules
{
    public static ushort Error(CharacterEmotionCatalog? emotions, TitleCatalog? titles, Character character, ushort id)
    {
        if (emotions?.Find(id) is not { } definition) return 1;
        if (emotions.CanSelect(id)) return 0;
        // 章节奖励可把隐藏的旧活动心情变为可选；显示列不等于永久禁止解锁。
        if (definition.Kind == 3 && titles is not null && titles.Sections.Any(pair =>
            pair.Value.Emotion == id && character.TitleSections.Contains(pair.Key))) return 0;
        if (definition.Kind == 5 && definition.RequiredQuests.Length != 0)
        {
            var completed = QuestEligibility.Completed(character);
            if (definition.RequiredQuests.All(quest => completed.Contains(quest) ||
                character.TitleProgress.GetValueOrDefault(quest)?.Remaining.Complete == true)) return 0;
        }
        // 3是本版已存在的选择失败提示；未知编号才使用“不存在”的1。
        return 3;
    }
}
