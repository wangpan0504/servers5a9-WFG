using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

internal static class QuestTerrainBypass
{
    // 只描述被深渊替代的任务地形，不暂停任务条件，不修改接取/进度。
    public static ImmutableArray<int> ForHell(Character character, DungeonDefinition dungeon, QuestCatalog? quests)
    {
        var connected = dungeon.Mazes.Where(row => row.ConnectionQuestId is { } id && row.HasActiveQuestConnection(id))
            .Select(row => row.ConnectionQuestId!.Value).ToHashSet();
        if (connected.Count == 0) return [];
        var result = ImmutableArray.CreateBuilder<int>();
        var completed = QuestEligibility.Completed(character);
        foreach (var active in character.ActiveMainQuests.Where(row => row.ConditionState != 0))
        {
            bool replaces = connected.Contains(active.QuestId);
            if (quests?.TryGet(active.QuestId, out var quest) == true)
            {
                replaces |= quest.MainQuest is { } parent && connected.Contains(parent);
                if (QuestRelationProgress.SequentialParent(quest, quests))
                    replaces |= connected.Contains(quest.IntData.FirstOrDefault(id => !completed.Contains(id)));
            }
            if (replaces) result.Add(active.QuestId);
        }
        return result.ToImmutable();
    }
}
